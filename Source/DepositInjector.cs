using System;
using System.Linq;
using Game.Info;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Manager;

namespace MyMod
{
    internal static class DepositInjector
    {
        internal static void Run(DepositConfig config, ObjectInfoManager oim)
        {
            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) { Plugin.Log.LogError("[DepositInjector] AllScriptableObjectManager is null"); return; }

            int added = 0, overwritten = 0, skipped = 0;

            foreach (var oi in oim.allObjectInfos)
            {
                var entries = config.GetDepositsFor(oi.ObjectName);
                if (entries == null || entries.Count == 0) continue;

                foreach (var entry in entries)
                {
                    var resource = allSO.AllResourceDefinitions.GetByID(entry.ResourceId);
                    if (resource == null)
                    {
                        Plugin.Log.LogWarning($"[DepositInjector] Unknown resourceId '{entry.ResourceId}' on '{oi.ObjectName}' — skipping.");
                        skipped++; continue;
                    }

                    if (!Enum.TryParse<RowResourcesData.EResourceState>(entry.State, out var state))
                    {
                        Plugin.Log.LogWarning($"[DepositInjector] Unknown state '{entry.State}' — valid: Solid, Liquid, Gas, Underground.");
                        skipped++; continue;
                    }

                    if (entry.Overwrite)
                    {
                        var existing = oi.ListRowResourcesData?
                            .FirstOrDefault(d => d.ResourcesType?.ID == entry.ResourceId && d.ResourceState == state);
                        if (existing != null)
                        {
                            existing.Value = entry.Amount;
                            if (entry.MiningFactor.HasValue) existing.MiningFactor = entry.MiningFactor.Value;
                            overwritten++;
                            Plugin.Log.LogDebug($"[DepositInjector] Overwrote {oi.ObjectName} {entry.ResourceId}/{entry.State} → {entry.Amount:N0}");
                            continue;
                        }
                    }

                    {
                        var deposit = new RowResourcesData
                        {
                            ResourcesType = resource,
                            ResourceState = state,
                            MiningFactor  = entry.MiningFactor ?? 0.5f,
                            ForcePrimary  = entry.ForcePrimary
                        };
                        oi.AddDeposit(deposit, fullyExploredForAll: true);
                        deposit.Value = entry.Amount;
                        added++;
                        Plugin.Log.LogDebug($"[DepositInjector] Added {oi.ObjectName} {entry.ResourceId}/{entry.State} = {entry.Amount:N0}");
                    }
                }
            }

            Plugin.Log.LogInfo($"[DepositInjector] Done — added: {added}, overwritten: {overwritten}, skipped: {skipped}");
        }
    }
}
