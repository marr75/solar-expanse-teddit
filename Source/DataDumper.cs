using System.Collections.Generic;
using System.IO;
using Data.ScriptableObject;
using Game.Info;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Manager;
using Newtonsoft.Json;
using ScriptableObjectScripts;

namespace MyMod
{
    /// <summary>
    /// Run-once tool: dumps all game data to BepInEx/plugins/MyMod/dump/ as separate files:
    ///   resources.json  — all resource definition IDs and translation keys
    ///   bodies.json     — all celestial bodies with subtype config and current deposits
    ///   facilities.json — all facility/module descriptors with current field values
    ///   spacecraft.json — all spacecraft type descriptors with current field values
    /// Disable by setting DumpOnLoad = false in Plugin after you have the data.
    /// </summary>
    internal static class DataDumper
    {
        internal static void Run(string dumpDir)
        {
            Plugin.Log.LogInfo("[DataDumper] Starting dump...");
            Directory.CreateDirectory(dumpDir);

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) throw new System.NullReferenceException("AllScriptableObjectManager.Instance is null");

            var oim = MonoBehaviourSingleton<ObjectInfoManager>.Instance;
            if (oim == null) throw new System.NullReferenceException("ObjectInfoManager.Instance is null");

            DumpResources(allSO, dumpDir);
            DumpBodies(oim, dumpDir);
            DumpFacilities(allSO, dumpDir);
            DumpSpacecraft(allSO, dumpDir);
            DumpLaunchVehicles(allSO, dumpDir);
            DumpResearch(allSO, dumpDir);

            Plugin.Log.LogInfo($"[DataDumper] Done → {dumpDir}");
        }

        // ── Resources ─────────────────────────────────────────────────────────

        static void DumpResources(AllScriptableObjectManager allSO, string dir)
        {
            var list = new List<ResourceDefEntry>();
            foreach (var res in allSO.AllResourceDefinitions.List)
                list.Add(new ResourceDefEntry { Id = res.ID, TranslationKey = res.IDToTranslate });

            Write(dir, "resources.json", list);
            Plugin.Log.LogInfo($"[DataDumper] resources.json — {list.Count} entries");
        }

        // ── Bodies ────────────────────────────────────────────────────────────

        static void DumpBodies(ObjectInfoManager oim, string dir)
        {
            var list = new List<BodyEntry>();
            foreach (var oi in oim.allObjectInfos)
            {
                var body = new BodyEntry { Name = oi.ObjectName };
                var sub = oi.objectSubType;
                if (sub != null)
                {
                    body.ObjectSubTypeId = sub.ID;
                    if (sub.MiningFactors != null)
                    {
                        foreach (var slot in sub.MiningFactors)
                        {
                            var slotEntries = new List<MiningFactorEntry>();
                            foreach (var mf in slot)
                                slotEntries.Add(new MiningFactorEntry
                                {
                                    ResourceId   = mf.ResourceDefinition?.ID,
                                    State        = mf.resourceState.ToString(),
                                    Category     = mf.Category.ToString(),
                                    Probability  = mf.probability,
                                    ForcePrimary = mf.forcePrimary
                                });
                            body.MiningFactorSlots.Add(slotEntries);
                        }
                    }
                    if (sub.ResourceRandomMap != null)
                    {
                        foreach (var kv in sub.ResourceRandomMap)
                            body.ResourceRandomMap.Add(new ResourceRandomMapEntry
                            {
                                ResourceId = kv.Key.definition?.ID,
                                State      = kv.Key.state.ToString(),
                                IsInfinite = kv.Value.isInfinite,
                                RangeMin   = kv.Value.range.x,
                                RangeMax   = kv.Value.range.y
                            });
                    }
                }
                if (oi.ListRowResourcesData != null)
                    foreach (var dep in oi.ListRowResourcesData)
                        body.CurrentDeposits.Add(new DepositEntry
                        {
                            ResourceId   = dep.ResourcesType?.ID,
                            State        = dep.ResourceState.ToString(),
                            Amount       = dep.Value,
                            MiningFactor = dep.MiningFactor,
                            ForcePrimary = dep.ForcePrimary
                        });
                list.Add(body);
            }

            Write(dir, "bodies.json", list);
            Plugin.Log.LogInfo($"[DataDumper] bodies.json — {list.Count} entries");
        }

        // ── Facilities ────────────────────────────────────────────────────────

        static void DumpFacilities(AllScriptableObjectManager allSO, string dir)
        {
            var list = new List<FacilityEntry>();
            foreach (var fd in allSO.AllFacility.List)
            {
                if (fd == null) continue;
                var entry = new FacilityEntry
                {
                    Id                      = fd.ID,
                    ActualType              = fd.GetType().Name,
                    FacilityType            = fd.facilityType.ToString(),
                    IsObsolete              = fd.IsObsolete,
                    IsLocked                = fd.IsLocked,
                    MaintenanceCostPerDay   = fd.MaintenanceCostPerDay,
                    EnergyConsumption       = fd.EnergyConsumption,
                    NeedWorkersToWork       = (int)fd.NeedWorkersToWork(null),
                    SpecialAbility          = fd.specialAbilityFacilityNew.ToString(),
                    SpecialAbilityParameter = fd.specialAbilityParameter,
                    UpkeepStacking          = fd.upkeepStacking,
                    UpkeepStackingValue     = fd.upkeepStackingValue,
                };
                if (fd.energyProductionData != null)
                    entry.EnergyProduction = fd.energyProductionData.energyProduction;
                try { entry.Name = fd.Name; } catch { entry.Name = fd.ID; }
                list.Add(entry);
            }

            Write(dir, "facilities.json", list);
            Plugin.Log.LogInfo($"[DataDumper] facilities.json — {list.Count} entries");
        }

        // ── Spacecraft ────────────────────────────────────────────────────────

        static void DumpSpacecraft(AllScriptableObjectManager allSO, string dir)
        {
            var list = new List<SpacecraftEntry>();
            foreach (var sc in allSO.AllSpacecraftType.List)
            {
                if (sc == null) continue;
                var entry = new SpacecraftEntry
                {
                    Id                    = sc.ID,
                    IsLocked              = sc.isLocked,
                    EngineType            = sc.engineType.ToString(),
                    Mass                  = sc.Mass,
                    CargoCapacity         = (float)sc.CargoCapacity,
                    FuelCapacity          = sc.FuelCapacity,
                    Thrust                = sc.Thrust,
                    ExhaustV              = sc.ExhaustV,
                    MaxLifeSupport        = sc.MAXLifeSupport,
                    AvailableDeltaV       = sc.AvailableDeltaV,
                    MaintenanceCostPerDay = sc.MaintenanceCostPerDay,
                    MaxPayload            = sc.MaxPayload,
                };
                try { entry.Name = sc.Name; } catch { entry.Name = sc.ID; }
                list.Add(entry);
            }

            Write(dir, "spacecraft.json", list);
            Plugin.Log.LogInfo($"[DataDumper] spacecraft.json — {list.Count} entries");
        }

        // ── Launch Vehicles ───────────────────────────────────────────────────

        static void DumpLaunchVehicles(AllScriptableObjectManager allSO, string dir)
        {
            var list = new List<LaunchVehicleEntry>();
            foreach (var lv in allSO.AllLaunchVehicleType.List)
            {
                if (lv == null) continue;
                var entry = new LaunchVehicleEntry
                {
                    Id                    = lv.ID,
                    IsLocked              = lv.isLocked,
                    MaxPayload            = lv.maxPayload,
                    MaxFuelLoad           = lv.maxFuelLoad,
                    CostLaunch            = lv.costLaunch,
                    ExhaustV              = lv.exhaustV,
                    Reusability           = lv.reusability,
                    MaintenanceCostPerDay = lv.MaintenanceCostPerDay,
                    CanSendHuman          = lv.canSendHuman,
                    FakeForFacility       = lv.FakeForFacility,
                };
                try { entry.Name = lv.Name; } catch { entry.Name = lv.ID; }
                list.Add(entry);
            }

            Write(dir, "launch_vehicles.json", list);
            Plugin.Log.LogInfo($"[DataDumper] launch_vehicles.json — {list.Count} entries");
        }

        // ── Research ──────────────────────────────────────────────────────────

        static void DumpResearch(AllScriptableObjectManager allSO, string dir)
        {
            var list = new List<ResearchEntry>();
            foreach (var rd in allSO.AllResearchDefinition.List)
            {
                if (rd == null) continue;
                var entry = new ResearchEntry
                {
                    Id                  = rd.ID,
                    IsLocked            = rd.isLocked,
                    IsLockedForUI       = rd.isLockedForUI,
                    WorkHourToComplete  = rd.WorkHourToComplete,
                    ResearchType        = rd.ResearchType?.ID,
                    ResearchSubType     = rd.ResearchSubType?.ID,
                    Stage               = rd.Stage,
                    Requirements        = new List<string>(),
                };
                foreach (var req in rd.RequirementsResearch)
                    if (req != null) entry.Requirements.Add(req.ID);
                try { entry.Title = rd.Title; } catch { entry.Title = rd.ID; }
                list.Add(entry);
            }

            Write(dir, "research.json", list);
            Plugin.Log.LogInfo($"[DataDumper] research.json — {list.Count} entries");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        static void Write(string dir, string filename, object data)
        {
            File.WriteAllText(
                Path.Combine(dir, filename),
                JsonConvert.SerializeObject(data, Formatting.Indented));
        }

        // ── Data classes ──────────────────────────────────────────────────────

        class ResourceDefEntry
        {
            public string Id             { get; set; }
            public string TranslationKey { get; set; }
        }

        class BodyEntry
        {
            public string                        Name              { get; set; }
            public string                        ObjectSubTypeId   { get; set; }
            public List<List<MiningFactorEntry>> MiningFactorSlots { get; set; } = new List<List<MiningFactorEntry>>();
            public List<ResourceRandomMapEntry>  ResourceRandomMap { get; set; } = new List<ResourceRandomMapEntry>();
            public List<DepositEntry>            CurrentDeposits   { get; set; } = new List<DepositEntry>();
        }

        class MiningFactorEntry
        {
            public string ResourceId   { get; set; }
            public string State        { get; set; }
            public string Category     { get; set; }
            public float  Probability  { get; set; }
            public bool   ForcePrimary { get; set; }
        }

        class ResourceRandomMapEntry
        {
            public string ResourceId { get; set; }
            public string State      { get; set; }
            public bool   IsInfinite { get; set; }
            public float  RangeMin   { get; set; }
            public float  RangeMax   { get; set; }
        }

        class DepositEntry
        {
            public string  ResourceId   { get; set; }
            public string  State        { get; set; }
            public double  Amount       { get; set; }
            public float?  MiningFactor { get; set; }
            public bool    ForcePrimary { get; set; }
        }

        class FacilityEntry
        {
            public string  Id                      { get; set; }
            public string  Name                    { get; set; }
            public string  ActualType              { get; set; }
            public string  FacilityType            { get; set; }
            public bool    IsObsolete              { get; set; }
            public bool    IsLocked                { get; set; }
            public float   MaintenanceCostPerDay   { get; set; }
            public double  EnergyConsumption       { get; set; }
            public int     NeedWorkersToWork       { get; set; }
            public string  SpecialAbility          { get; set; }
            public float   SpecialAbilityParameter { get; set; }
            public bool    UpkeepStacking          { get; set; }
            public float   UpkeepStackingValue     { get; set; }
            public double? EnergyProduction        { get; set; }
        }

        class SpacecraftEntry
        {
            public string  Id                    { get; set; }
            public string  Name                  { get; set; }
            public bool    IsLocked              { get; set; }
            public string  EngineType            { get; set; }
            public float   Mass                  { get; set; }
            public float   CargoCapacity         { get; set; }
            public float   FuelCapacity          { get; set; }
            public float   Thrust                { get; set; }
            public float   ExhaustV              { get; set; }
            public float   MaxLifeSupport        { get; set; }
            public float   AvailableDeltaV       { get; set; }
            public float   MaintenanceCostPerDay { get; set; }
            public float?  MaxPayload            { get; set; }
        }

        class ResearchEntry
        {
            public string       Id                 { get; set; }
            public string       Title              { get; set; }
            public bool         IsLocked           { get; set; }
            public bool         IsLockedForUI      { get; set; }
            public float        WorkHourToComplete { get; set; }
            public string       ResearchType       { get; set; }
            public string       ResearchSubType    { get; set; }
            public int          Stage              { get; set; }
            public List<string> Requirements       { get; set; }
        }

        class LaunchVehicleEntry
        {
            public string  Id                    { get; set; }
            public string  Name                  { get; set; }
            public bool    IsLocked              { get; set; }
            public float   MaxPayload            { get; set; }
            public float   MaxFuelLoad           { get; set; }
            public float   CostLaunch            { get; set; }
            public float   ExhaustV              { get; set; }
            public float   Reusability           { get; set; }
            public float   MaintenanceCostPerDay { get; set; }
            public bool    CanSendHuman          { get; set; }
            public bool    FakeForFacility       { get; set; }
        }
    }
}
