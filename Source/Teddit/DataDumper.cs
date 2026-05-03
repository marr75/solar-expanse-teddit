using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Data.ScriptableObject;
using Game;
using Game.Info;
using Game.ObjectInfoDataScripts.CustomFacilitiesAndModules;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Game.UI.Windows.Elements.SpaceCraftConstructElements;
using Manager;
using ScriptableObjectScripts;
using UnityEngine;

namespace Teddit
{
    /// <summary>
    /// Run-once tool: dumps all game data to BepInEx/plugins/Teddit/dump/ as separate YAML files:
    ///   resources.yaml  — all resource definition IDs and translation keys
    ///   bodies.yaml     — all celestial bodies with subtype config and current deposits
    ///   facilities.yaml — all facility/module descriptors in patch-compatible format
    ///   spacecraft.yaml — all spacecraft type descriptors in patch-compatible format
    ///   launch_vehicles.yaml — all launch vehicle descriptors in patch-compatible format
    ///   research.yaml   — all research definitions
    ///   deposits.yaml   — all body deposits in patch-compatible format
    /// Dump format == patch format: keyed by ID, camelCase field names, copy-pasteable.
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
            DumpDeposits(oim, dumpDir);
            DumpFacilities(allSO, dumpDir);
            DumpSpacecraft(allSO, dumpDir);
            DumpLaunchVehicles(allSO, dumpDir);
            DumpResearch(allSO, dumpDir);

            Plugin.Log.LogInfo($"[DataDumper] Done → {dumpDir}");
        }

        // ── Resources ─────────────────────────────────────────────────────────

        static void DumpResources(AllScriptableObjectManager allSO, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var res in allSO.AllResourceDefinitions.List)
            {
                if (res == null) continue;
                string name = res.ID;
                try { name = res.Name; } catch { }
                sb.AppendLine($"# {name}");
                sb.AppendLine($"{YamlKey(res.ID)}:");
                sb.AppendLine($"  resourceType: {YamlScalar(res.ResourceType.ToString())}");
                sb.AppendLine($"  showOnUI: {FormatBool(res.ShowOnUI)}");
                sb.AppendLine($"  canBeLeftOnObject: {FormatBool(res.CanBeLeftOnObject)}");
                sb.AppendLine($"  marketClearingPriceBase: {FormatDouble(res.MarketClearingPriceBase)}");
                sb.AppendLine($"  priceChangeMultiplier: {FormatDouble(res.PriceChangeMultiplier)}");
                sb.AppendLine($"  startingSummedPreviousTradesQuantity: {FormatFloat(res.StartingSummedPreviousTradesQuantity)}");
                sb.AppendLine($"  startingPreviousTradesCount: {FormatFloat(res.StartingPreviousTradesCount)}");
                sb.AppendLine($"  toSortMarketOffer: {res.ToSortMarketOffer}");
                sb.AppendLine($"  showInMarket: {FormatBool(allSO.AllResourceDefinitions.ListResourceDefinitionInMarketPlaceOffer.Contains(res))}");
                string iconRef = FacilityCreator.GetSpriteReference(res.Sprite);
                if (!string.IsNullOrEmpty(iconRef))
                    sb.AppendLine($"  iconRef: {YamlScalar(iconRef)}");
                AppendResourceTerraformationInfo(sb, "  terraformationInfo", res.TerraformationInfo);
                AppendAnimationCurve(sb, "  toxicityCurve", res.ToxicityCurve);
                sb.AppendLine();
                count++;
            }
            File.WriteAllText(Path.Combine(dir, "resources.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] resources.yaml — {count} entries");
        }

        // ── Bodies ────────────────────────────────────────────────────────────

        static void DumpBodies(ObjectInfoManager oim, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var oi in oim.allObjectInfos)
            {
                sb.AppendLine($"# {oi.ObjectName}");
                sb.AppendLine($"{YamlKey(oi.ObjectName)}:");

                var sub = oi.objectSubType;
                if (sub != null)
                {
                    sb.AppendLine($"  objectSubTypeId: {YamlScalar(sub.ID)}");

                    // MiningFactorSlots
                    if (sub.MiningFactors != null && sub.MiningFactors.Count > 0)
                    {
                        sb.AppendLine("  miningFactorSlots:");
                        foreach (var slot in sub.MiningFactors)
                        {
                            sb.AppendLine("    -");
                            foreach (var mf in slot)
                            {
                                sb.AppendLine($"      - resourceId: {YamlScalar(mf.ResourceDefinition?.ID)}");
                                sb.AppendLine($"        state: {YamlScalar(mf.resourceState.ToString())}");
                                sb.AppendLine($"        category: {YamlScalar(mf.Category.ToString())}");
                                sb.AppendLine($"        probability: {FormatFloat(mf.probability)}");
                                sb.AppendLine($"        forcePrimary: {FormatBool(mf.forcePrimary)}");
                            }
                        }
                    }

                    // ResourceRandomMap
                    if (sub.ResourceRandomMap != null && sub.ResourceRandomMap.Count > 0)
                    {
                        sb.AppendLine("  resourceRandomMap:");
                        foreach (var kv in sub.ResourceRandomMap)
                        {
                            sb.AppendLine($"    - resourceId: {YamlScalar(kv.Key.definition?.ID)}");
                            sb.AppendLine($"      state: {YamlScalar(kv.Key.state.ToString())}");
                            sb.AppendLine($"      isInfinite: {FormatBool(kv.Value.isInfinite)}");
                            sb.AppendLine($"      rangeMin: {FormatFloat(kv.Value.range.x)}");
                            sb.AppendLine($"      rangeMax: {FormatFloat(kv.Value.range.y)}");
                        }
                    }
                }

                // Current deposits — in patch-compatible format
                if (oi.ListRowResourcesData != null && oi.ListRowResourcesData.Count > 0)
                {
                    sb.AppendLine("  currentDeposits:");
                    foreach (var dep in oi.ListRowResourcesData)
                    {
                        var explored = FindPreferredExploredRow(oi, dep);
                        sb.AppendLine($"    - resourceId: {YamlScalar(dep.ResourcesType?.ID)}");
                        sb.AppendLine($"      state: {YamlScalar(dep.ResourceState.ToString())}");
                        sb.AppendLine($"      amount: {FormatDouble(dep.Value)}");
                        sb.AppendLine($"      miningFactor: {FormatNullableFloat(dep.MiningFactor)}");
                        if (explored != null)
                        {
                            sb.AppendLine($"      explorationLevel: {FormatDouble(explored.Value)}");
                            sb.AppendLine($"      preliminaryExplored: {FormatBool(explored.PreliminaryExplored)}");
                        }
                        sb.AppendLine($"      forcePrimary: {FormatBool(dep.ForcePrimary)}");
                    }
                }

                sb.AppendLine();
                count++;
            }
            File.WriteAllText(Path.Combine(dir, "bodies.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] bodies.yaml — {count} entries");
        }

        // ── Deposits ──────────────────────────────────────────────────────────

        static void DumpDeposits(ObjectInfoManager oim, string dir)
        {
            var sb = new StringBuilder();
            int bodyCount = 0, depCount = 0;
            foreach (var oi in oim.allObjectInfos)
            {
                if (oi.ListRowResourcesData == null || oi.ListRowResourcesData.Count == 0)
                    continue;

                sb.AppendLine($"# {oi.ObjectName}");
                sb.AppendLine($"{YamlKey(oi.ObjectName)}:");
                foreach (var dep in oi.ListRowResourcesData)
                {
                    var explored = FindPreferredExploredRow(oi, dep);
                    sb.AppendLine($"  - resourceId: {YamlScalar(dep.ResourcesType?.ID)}");
                    sb.AppendLine($"    state: {YamlScalar(dep.ResourceState.ToString())}");
                    sb.AppendLine($"    amount: {FormatDouble(dep.Value)}");
                    sb.AppendLine($"    miningFactor: {FormatNullableFloat(dep.MiningFactor)}");
                    if (explored != null)
                    {
                        sb.AppendLine($"    explorationLevel: {FormatDouble(explored.Value)}");
                        sb.AppendLine($"    preliminaryExplored: {FormatBool(explored.PreliminaryExplored)}");
                    }
                    sb.AppendLine($"    forcePrimary: {FormatBool(dep.ForcePrimary)}");
                    depCount++;
                }
                sb.AppendLine();
                bodyCount++;
            }
            File.WriteAllText(Path.Combine(dir, "deposits.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] deposits.yaml — {bodyCount} bodies, {depCount} deposits");
        }

        // ── Facilities ────────────────────────────────────────────────────────

        static readonly FieldInfo _timeToBuildFi = typeof(Data.ScriptableObject.MyIDScriptableObjectProductionItem)
            .GetField("timeToBuildInDays", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _ctorEquipFi = typeof(Data.ScriptableObject.MyIDScriptableObjectProductionItem)
            .GetField("constructionEquipmentCountIsRequired", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _priceFi = ScriptableObjectPatcher.FindField(typeof(FacilityBaseDescriptor), "price");
        static readonly FieldInfo _refinerFi = ScriptableObjectPatcher.FindField(typeof(FacilityBaseDescriptor), "refinerData");
        static readonly FieldInfo _resourcesToMineFi = ScriptableObjectPatcher.FindField(typeof(FacilityBaseDescriptor), "resourcesToMine");
        static readonly FieldInfo _byproductsFi = ScriptableObjectPatcher.FindField(typeof(FacilityBaseDescriptor), "byproducts");

        static void DumpFacilities(AllScriptableObjectManager allSO, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var fd in allSO.AllFacility.List)
            {
                if (fd == null) continue;

                string name = fd.ID;
                try { name = fd.Name; } catch { }

                // Comment header
                sb.AppendLine($"# {name.ToUpperInvariant()} ({fd.GetType().Name})");
                sb.AppendLine($"{YamlKey(fd.ID)}:");

                sb.AppendLine($"  facilityType: {YamlScalar(fd.facilityType.ToString())}");
                sb.AppendLine($"  possiblePlacement: {YamlScalar(fd.PossiblePlacement.ToString())}");
                sb.AppendLine($"  maintenanceCostPerDay: {FormatFloat(fd.MaintenanceCostPerDay)}");
                sb.AppendLine($"  energyConsumption: {FormatDouble(fd.EnergyConsumption)}");
                sb.AppendLine($"  needWorkersToWork: {(int)fd.NeedWorkersToWork(null)}");
                sb.AppendLine($"  specialAbility: {YamlScalar(fd.specialAbilityFacilityNew.ToString())}");
                sb.AppendLine($"  specialAbilityParameter: {FormatFloat(fd.specialAbilityParameter)}");
                sb.AppendLine($"  upkeepStacking: {FormatBool(fd.upkeepStacking)}");
                sb.AppendLine($"  upkeepStackingValue: {FormatFloat(fd.upkeepStackingValue)}");
                sb.AppendLine($"  blockStacking: {FormatBool(fd.BlockStacking)}");
                string facilityIconRef = FacilityCreator.GetSpriteReference(fd.Sprite);
                if (!string.IsNullOrEmpty(facilityIconRef))
                    sb.AppendLine($"  iconRef: {YamlScalar(facilityIconRef)}");

                float timeToBuild = _timeToBuildFi != null ? (float)_timeToBuildFi.GetValue(fd) : 0f;
                bool ctorEquip   = _ctorEquipFi  != null ? (bool)_ctorEquipFi.GetValue(fd)   : true;
                sb.AppendLine($"  timeToBuildInDays: {FormatFloat(timeToBuild)}");
                sb.AppendLine($"  constructionEquipmentCountIsRequired: {FormatBool(ctorEquip)}");
                sb.AppendLine($"  facilityItemClass: {YamlScalar(fd.FacilityItemClass?.Name)}");

                // Price
                double buildCost = 0;
                var buildResources = new Dictionary<string, double>();
                if (_priceFi != null && _priceFi.GetValue(fd) is ResourcePrice price)
                {
                    buildCost = price.BuildCost;
                    if (price.ListResources != null)
                        foreach (var r in price.ListResources)
                            if (r?.ResourceDefinition != null)
                                buildResources[r.ResourceDefinition.ID] = r.Price;
                }
                sb.AppendLine($"  buildCost: {FormatDouble(buildCost)}");
                AppendDict(sb, "  buildResources", buildResources);

                // EnergyProductionData
                if (fd.energyProductionData != null)
                {
                    sb.AppendLine($"  energyProduction: {FormatNullableDouble(fd.energyProductionData.energyProduction)}");
                    sb.AppendLine($"  solarPanels: {FormatBool(fd.energyProductionData.solarPanels)}");
                    sb.AppendLine($"  windPower: {FormatBool(fd.energyProductionData.windPower)}");
                    sb.AppendLine($"  geothermal: {FormatBool(fd.energyProductionData.geothermalPower)}");
                    var energyInput = new Dictionary<string, double>();
                    if (fd.energyProductionData.input != null)
                        foreach (var inp in fd.energyProductionData.input)
                            if (inp?.resource != null)
                                energyInput[inp.resource.ID] = inp.ratePerDay;
                    AppendDict(sb, "  energyInput", energyInput);
                }
                if (fd is GroundFacilityDescriptor gfd)
                {
                    if (Math.Abs(gfd.resourceExplorationBonus) > 0.0001f)
                        sb.AppendLine($"  resourceExplorationBonus: {FormatFloat(gfd.resourceExplorationBonus)}");

                    bool isLab = fd.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.Lab)
                              || fd.FacilityItemClass == typeof(LabFacility);
                    if (isLab && gfd.labData != null)
                    {
                        sb.AppendLine($"  labBonusToResearchInPerHour: {gfd.labData.bonusToResearchInPerHour}");
                        sb.AppendLine($"  labResearchSubTypeId: {YamlScalar(gfd.labData.idResearchSubType)}");
                        AppendStringList(sb, "  labIdToBonus", gfd.labData.idToBonus?.ToList() ?? new List<string>());
                    }
                }

                // ResourcesToMine
                var resourcesToMine = new List<string>();
                if (_resourcesToMineFi != null && _resourcesToMineFi.GetValue(fd) is HashSet<ResourceDefinition> mines)
                    foreach (var r in mines)
                        if (r != null) resourcesToMine.Add(r.ID);
                AppendStringList(sb, "  resourcesToMine", resourcesToMine);

                // RefinerData
                var refinerInput  = new Dictionary<string, double>();
                var refinerOutput = new Dictionary<string, double>();
                if (fd.specialAbilityFacilityNew.HasFlag(ESpecialAbilityFacilityNew.Refiner) && _refinerFi != null)
                {
                    object rd = _refinerFi.GetValue(fd);
                    if (rd != null)
                    {
                        Type rdType = rd.GetType();
                        DumpRefinerList(rd, rdType, "Input",  refinerInput);
                        DumpRefinerList(rd, rdType, "Output", refinerOutput);
                    }
                }
                AppendDict(sb, "  refinerInput",  refinerInput);
                AppendDict(sb, "  refinerOutput", refinerOutput);

                // Byproducts
                var byproducts = new List<ByproductEntry>();
                if (_byproductsFi != null && _byproductsFi.GetValue(fd) is FacilityBaseDescriptor.Byproduct[] bps)
                    foreach (var bp in bps)
                        if (bp?.resource != null)
                            byproducts.Add(new ByproductEntry
                            {
                                Resource = bp.resource.ID,
                                Rate     = bp.rate,
                                State    = bp.state.ToString(),
                            });
                AppendByproducts(sb, "  byproducts", byproducts);

                sb.AppendLine();
                count++;
            }

            File.WriteAllText(Path.Combine(dir, "facilities.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] facilities.yaml — {count} entries");
        }

        static void DumpRefinerList(object refinerData, Type rdType, string memberName, Dictionary<string, double> target)
        {
            try
            {
                object listObj = GetRefinerMemberValue(refinerData, rdType, memberName);
                if (!(listObj is IEnumerable items)) return;
                foreach (object item in items)
                {
                    if (item == null) continue;
                    var resFi      = item.GetType().GetField("resource");
                    var rateFi     = item.GetType().GetField("ratePerDay");
                    var res        = resFi?.GetValue(item) as ResourceDefinition;
                    var rate       = rateFi != null ? (double)rateFi.GetValue(item) : 0.0;
                    if (res != null) target[res.ID] = rate;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DataDumper] DumpRefinerList({memberName}): {ex.Message}");
            }
        }

        static object GetRefinerMemberValue(object refinerData, Type rdType, string memberName)
        {
            string fieldName = char.ToLowerInvariant(memberName[0]) + memberName.Substring(1);
            var fi = rdType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                  ?? rdType.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi != null) return fi.GetValue(refinerData);

            var prop = rdType.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return prop?.GetValue(refinerData, null);
        }

        // ── Spacecraft ────────────────────────────────────────────────────────

        static readonly FieldInfo _scHullFi    = typeof(Data.ScriptableObject.SpacecraftType)
            .GetField("hull", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _scIsLockedFi = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.SpacecraftType), "isLocked");
        static readonly FieldInfo _lvPriceFi    = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.LaunchVehicleType), "priceBase");

        static void DumpSpacecraft(AllScriptableObjectManager allSO, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var sc in allSO.AllSpacecraftType.List)
            {
                if (sc == null) continue;

                string name = sc.ID;
                try { name = sc.Name; } catch { }

                sb.AppendLine($"# {name.ToUpperInvariant()} (SpacecraftType)");
                sb.AppendLine($"{YamlKey(sc.ID)}:");

                sb.AppendLine($"  engineType: {YamlScalar(sc.engineType.ToString())}");
                sb.AppendLine($"  orbitSC: {FormatBool(sc.OrbitSC)}");
                sb.AppendLine($"  solarSC: {FormatBool(sc.SolarSC)}");
                sb.AppendLine($"  solarParameter: {FormatFloat(sc.SolarParameter)}");
                sb.AppendLine($"  isLocked: {FormatBool(_scIsLockedFi != null && (bool)_scIsLockedFi.GetValue(sc))}");
                sb.AppendLine($"  needLaunchVehicleToGoToMoon: {FormatBool(sc.needLaunchVehicleToGoToMoon)}");
                sb.AppendLine($"  buildOnlyLowOrbit: {FormatBool(sc.BuildOnlyLowOrbit)}");
                sb.AppendLine($"  canByBuildByUser: {FormatBool(sc.CanByBuildByUser)}");
                sb.AppendLine($"  lowOrbitContainer: {FormatBool(sc.LowOrbitContainer)}");
                sb.AppendLine($"  magneticCatapult: {FormatBool(sc.MagneticCatapult)}");
                sb.AppendLine($"  reusability: {FormatFloat(sc.Reusability)}");
                sb.AppendLine($"  constanceAcceleration: {FormatBool(sc.ConstanceAcceleration)}");
                sb.AppendLine($"  isInterstellarShip: {FormatBool(sc.IsInterstellarShip)}");
                sb.AppendLine($"  asteroidPullingShip: {FormatBool(sc.AsteroidPullingShip)}");
                var spacecraftFuel = sc.GetFuelType();
                if (spacecraftFuel != null)
                    sb.AppendLine($"  fuelType: {YamlScalar(spacecraftFuel.ID)}");
                string spacecraftIconRef = FacilityCreator.GetSpriteReference(sc.RocketBackGround);
                if (!string.IsNullOrEmpty(spacecraftIconRef))
                    sb.AppendLine($"  iconRef: {YamlScalar(spacecraftIconRef)}");
                sb.AppendLine($"  timeToBuildInDays: {FormatFloat(_timeToBuildFi != null ? (float)_timeToBuildFi.GetValue(sc) : 0f)}");
                sb.AppendLine($"  constructionEquipmentCountIsRequired: {FormatBool(_ctorEquipFi != null ? (bool)_ctorEquipFi.GetValue(sc) : true)}");

                double buildCost = 0;
                var buildResources = new Dictionary<string, double>();
                {
                    // priceBase may live on the hull object for completed-design spacecraft
                    object priceSource = sc;
                    object hullBoxed = _scHullFi?.GetValue(sc);
                    if (hullBoxed != null && ScriptableObjectPatcher.FindField(hullBoxed.GetType(), "priceBase") != null)
                        priceSource = hullBoxed;
                    var priceFi = ScriptableObjectPatcher.FindField(priceSource.GetType(), "priceBase");
                    if (priceFi?.GetValue(priceSource) is ResourcePrice price)
                    {
                        buildCost = price.BuildCost;
                        if (price.ListResources != null)
                            foreach (var r in price.ListResources)
                                if (r?.ResourceDefinition != null)
                                    buildResources[r.ResourceDefinition.ID] = r.Price;
                    }
                }
                sb.AppendLine($"  buildCost: {FormatDouble(buildCost)}");
                AppendDict(sb, "  buildResources", buildResources);

                sb.AppendLine($"  cargoCapacity: {FormatFloat((float)sc.CargoCapacity)}");
                sb.AppendLine($"  fuelCapacity: {FormatFloat(sc.FuelCapacity)}");
                sb.AppendLine($"  thrust: {FormatFloat(sc.Thrust)}");
                sb.AppendLine($"  exhaustV: {FormatFloat(sc.ExhaustV)}");
                sb.AppendLine($"  mass: {FormatFloat(sc.Mass)}");
                sb.AppendLine($"  maxLifeSupport: {FormatFloat(sc.MAXLifeSupport)}");
                sb.AppendLine($"  availableDeltaV: {FormatFloat(sc.AvailableDeltaV)}");
                sb.AppendLine($"  maximumAcceleration: {FormatFloat(sc.MaximumAcceleration)}");
                sb.AppendLine($"  maintenanceCostPerDay: {FormatFloat(sc.MaintenanceCostPerDay)}");
                sb.AppendLine($"  maxPayload: {FormatNullableFloat(sc.MaxPayload)}");

                sb.AppendLine();
                count++;
            }

            File.WriteAllText(Path.Combine(dir, "spacecraft.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] spacecraft.yaml — {count} entries");
        }

        // ── Launch Vehicles ───────────────────────────────────────────────────

        static readonly FieldInfo _lvIsLockedFi        = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.LaunchVehicleType), "isLocked");
        static readonly FieldInfo _lvForCycleMissionFi  = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.LaunchVehicleType), "forCycleMission");
        static readonly FieldInfo _lvFakeForFacilityFi  = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.LaunchVehicleType), "fakeForFacility");
        static readonly FieldInfo _lvCanBuyMaxPayloadFi = ScriptableObjectPatcher.FindField(typeof(Data.ScriptableObject.LaunchVehicleType), "canBuyMaxPayload");

        static void DumpLaunchVehicles(AllScriptableObjectManager allSO, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var lv in allSO.AllLaunchVehicleType.List)
            {
                if (lv == null) continue;

                string name = lv.ID;
                try { name = lv.Name; } catch { }

                sb.AppendLine($"# {name.ToUpperInvariant()} (LaunchVehicleType)");
                sb.AppendLine($"{YamlKey(lv.ID)}:");

                sb.AppendLine($"  isLocked: {FormatBool(_lvIsLockedFi != null && (bool)_lvIsLockedFi.GetValue(lv))}");
                sb.AppendLine($"  special: {YamlScalar(lv.special.ToString())}");
                sb.AppendLine($"  canSendHuman: {FormatBool(lv.canSendHuman)}");
                if (lv.FuelTypeOnStart != null)
                    sb.AppendLine($"  fuelType: {YamlScalar(lv.FuelTypeOnStart.ID)}");
                string launchVehicleIconRef = FacilityCreator.GetSpriteReference(FindFieldValue<Sprite>(lv, "rocketBackGround"));
                if (!string.IsNullOrEmpty(launchVehicleIconRef))
                    sb.AppendLine($"  iconRef: {YamlScalar(launchVehicleIconRef)}");
                sb.AppendLine($"  forCycleMission: {FormatBool(_lvForCycleMissionFi != null && (bool)_lvForCycleMissionFi.GetValue(lv))}");
                sb.AppendLine($"  fakeForFacility: {FormatBool(_lvFakeForFacilityFi != null && (bool)_lvFakeForFacilityFi.GetValue(lv))}");
                sb.AppendLine($"  timeToBuildInDays: {FormatFloat(_timeToBuildFi != null ? (float)_timeToBuildFi.GetValue(lv) : 0f)}");
                sb.AppendLine($"  constructionEquipmentCountIsRequired: {FormatBool(_ctorEquipFi != null ? (bool)_ctorEquipFi.GetValue(lv) : true)}");

                double buildCost = 0;
                var buildResources = new Dictionary<string, double>();
                if (_lvPriceFi?.GetValue(lv) is ResourcePrice price)
                {
                    buildCost = price.BuildCost;
                    if (price.ListResources != null)
                        foreach (var r in price.ListResources)
                            if (r?.ResourceDefinition != null)
                                buildResources[r.ResourceDefinition.ID] = r.Price;
                }
                sb.AppendLine($"  buildCost: {FormatDouble(buildCost)}");
                AppendDict(sb, "  buildResources", buildResources);

                sb.AppendLine($"  maxPayload: {FormatFloat(lv.maxPayload)}");
                sb.AppendLine($"  maxFuelLoad: {FormatFloat(lv.maxFuelLoad)}");
                sb.AppendLine($"  costLaunch: {FormatFloat(lv.costLaunch)}");
                sb.AppendLine($"  exhaustV: {FormatFloat(lv.exhaustV)}");
                sb.AppendLine($"  reusability: {FormatFloat(lv.reusability)}");
                sb.AppendLine($"  maintenanceCostPerDay: {FormatFloat(lv.MaintenanceCostPerDay)}");

                float? canBuyMaxPayload = _lvCanBuyMaxPayloadFi?.GetValue(lv) as float?;
                sb.AppendLine($"  canBuyMaxPayload: {FormatNullableFloat(canBuyMaxPayload)}");

                sb.AppendLine();
                count++;
            }

            File.WriteAllText(Path.Combine(dir, "launch_vehicles.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] launch_vehicles.yaml — {count} entries");
        }

        // ── Research ──────────────────────────────────────────────────────────

        static void DumpResearch(AllScriptableObjectManager allSO, string dir)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (var rd in allSO.AllResearchDefinition.List)
            {
                if (rd == null) continue;

                string title = rd.ID;
                try { title = rd.Title; } catch { }

                sb.AppendLine($"# {title.ToUpperInvariant()} (ResearchDefinition)");
                sb.AppendLine($"{YamlKey(rd.ID)}:");

                sb.AppendLine($"  isLocked: {FormatBool(rd.isLocked)}");
                sb.AppendLine($"  isLockedForUI: {FormatBool(rd.isLockedForUI)}");
                sb.AppendLine($"  workHourToComplete: {FormatFloat(rd.WorkHourToComplete)}");
                sb.AppendLine($"  researchType: {YamlScalar(rd.ResearchType?.ID)}");
                sb.AppendLine($"  researchSubType: {YamlScalar(rd.ResearchSubType?.ID)}");
                sb.AppendLine($"  stage: {rd.Stage}");
                int subStage = _rdSubStageFi != null ? (int)_rdSubStageFi.GetValue(rd) : 0;
                sb.AppendLine($"  subStage: {subStage}");
                bool showInTree = _rdShowInTreeFi != null && (bool)_rdShowInTreeFi.GetValue(rd);
                sb.AppendLine($"  showInTree: {FormatBool(showInTree)}");
                var rdParent = _rdParentFi?.GetValue(rd) as ResearchDefinition;
                sb.AppendLine($"  newViewResearchTreeParent: {YamlScalar(rdParent?.ID)}");

                var reqs = new List<string>();
                foreach (var req in rd.RequirementsResearch)
                    if (req != null) reqs.Add(req.ID);
                AppendStringList(sb, "  requirementsResearch", reqs);

                AppendResearchUnlocks(sb, rd);

                sb.AppendLine();
                count++;
            }

            File.WriteAllText(Path.Combine(dir, "research.yaml"), sb.ToString());
            Plugin.Log.LogInfo($"[DataDumper] research.yaml — {count} entries");
        }

        static readonly FieldInfo _rdSubStageFi       = ScriptableObjectPatcher.FindField(typeof(ResearchDefinition), "subStage");
        static readonly FieldInfo _rdShowInTreeFi     = ScriptableObjectPatcher.FindField(typeof(ResearchDefinition), "showInTree");
        static readonly FieldInfo _rdParentFi         = ScriptableObjectPatcher.FindField(typeof(ResearchDefinition), "newViewResearchTreeParent");
        static readonly FieldInfo _rdUnlockDataFi     = ScriptableObjectPatcher.FindField(typeof(ResearchDefinition), "unlockData");
        static readonly FieldInfo _rdUnlockDataListFi  = ScriptableObjectPatcher.FindField(typeof(ResearchDefinition), "unlockDataList");
        static readonly Type      _udType              = typeof(ResearchDefinition).Assembly.GetType("Game.CompanyScripts.UnlockData");
        static readonly FieldInfo _udActionFi          = _udType?.GetField("actionUnlock", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _udParam1Fi          = _udType?.GetField("parameter1",   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        static void AppendResearchUnlocks(StringBuilder sb, ResearchDefinition rd)
        {
            var entries = new List<(string action, string id)>();

            object single = _rdUnlockDataFi?.GetValue(rd);
            if (single != null)
            {
                string p1 = _udParam1Fi?.GetValue(single) as string;
                if (!string.IsNullOrEmpty(p1))
                    entries.Add((_udActionFi?.GetValue(single)?.ToString() ?? "UnlockFacility", p1));
            }

            object listObj = _rdUnlockDataListFi?.GetValue(rd);
            if (listObj is Array arr)
            {
                foreach (var item in arr)
                {
                    if (item == null) continue;
                    string p1 = _udParam1Fi?.GetValue(item) as string;
                    if (!string.IsNullOrEmpty(p1))
                        entries.Add((_udActionFi?.GetValue(item)?.ToString() ?? "UnlockFacility", p1));
                }
            }

            if (entries.Count == 0) return;
            sb.AppendLine("  unlocks:");
            foreach (var (action, id) in entries)
            {
                sb.AppendLine($"    - action: {YamlScalar(action)}");
                sb.AppendLine($"      id: {YamlScalar(id)}");
            }
        }

        // ── YAML emit helpers ─────────────────────────────────────────────────

        static void AppendResourceTerraformationInfo(StringBuilder sb, string keyWithIndent, ResourceDefinition.TerraformationInfoDef info)
        {
            sb.AppendLine($"{keyWithIndent}:");
            string indent = new string(' ', keyWithIndent.Length - keyWithIndent.TrimStart().Length + 2);
            sb.AppendLine($"{indent}resourceOpticalDepthParameter: {FormatDouble(info.resourceOpticalDepthParameter)}");
            sb.AppendLine($"{indent}resourceHeatCapacity: {FormatDouble(info.resourceHeatCapacity)}");
            sb.AppendLine($"{indent}vaporizationLatentHeat: {FormatDouble(info.vaporizationLatentHeat)}");
            sb.AppendLine($"{indent}baseTemperatureBoiling: {FormatDouble(info.baseTemperatureBoiling)}");
            sb.AppendLine($"{indent}temperatureMelting: {FormatDouble(info.temperatureMelting)}");
            sb.AppendLine($"{indent}pressureTriplePoint: {FormatDouble(info.pressureTriplePoint)}");
        }

        static void AppendAnimationCurve(StringBuilder sb, string keyWithIndent, AnimationCurve curve)
        {
            if (curve == null || curve.keys == null || curve.keys.Length == 0)
                return;

            sb.AppendLine($"{keyWithIndent}:");
            string indent = new string(' ', keyWithIndent.Length - keyWithIndent.TrimStart().Length + 2);
            sb.AppendLine($"{indent}preWrapMode: {YamlScalar(curve.preWrapMode.ToString())}");
            sb.AppendLine($"{indent}postWrapMode: {YamlScalar(curve.postWrapMode.ToString())}");
            sb.AppendLine($"{indent}keys:");
            foreach (var frame in curve.keys)
            {
                sb.AppendLine($"{indent}  - time: {FormatFloat(frame.time)}");
                sb.AppendLine($"{indent}    value: {FormatFloat(frame.value)}");
                sb.AppendLine($"{indent}    inTangent: {FormatFloat(frame.inTangent)}");
                sb.AppendLine($"{indent}    outTangent: {FormatFloat(frame.outTangent)}");
                sb.AppendLine($"{indent}    weightedMode: {YamlScalar(frame.weightedMode.ToString())}");
                sb.AppendLine($"{indent}    inWeight: {FormatFloat(frame.inWeight)}");
                sb.AppendLine($"{indent}    outWeight: {FormatFloat(frame.outWeight)}");
            }
        }

        static T FindFieldValue<T>(object target, string fieldName) where T : class
        {
            if (target == null) return null;
            var fi = ScriptableObjectPatcher.FindField(target.GetType(), fieldName);
            return fi?.GetValue(target) as T;
        }

        static RowExploredResourcesData FindPreferredExploredRow(ObjectInfo oi, RowResourcesData deposit)
        {
            if (oi?.ObjectsInfoData == null || deposit == null)
                return null;

            var player = MonoBehaviourSingleton<GameManager>.Instance?.Player;
            var preferredData = player != null
                ? oi.ObjectsInfoData.FirstOrDefault(d => d?.company == player)
                : null;
            if (preferredData == null)
                preferredData = oi.ObjectsInfoData.FirstOrDefault(d => d != null);
            if (preferredData?.listExploredResourcesRows == null)
                return null;

            return preferredData.listExploredResourcesRows.FirstOrDefault(r => r?.ObservedData == deposit);
        }

        /// <summary>Returns a YAML-safe scalar. Quotes the value if it contains special chars.</summary>
        static string YamlScalar(string value)
        {
            if (value == null) return "null";
            if (value.Length == 0) return "\"\"";
            // Quote if starts with special char or contains colon-space, #, or other YAML metacharacters
            bool needsQuotes = value[0] == '\'' || value[0] == '"' || value[0] == '&'
                            || value[0] == '*'  || value[0] == '!' || value[0] == '|'
                            || value[0] == '>'  || value[0] == '%' || value[0] == '@'
                            || value[0] == '`'  || value[0] == '{'  || value[0] == '['
                            || value.Contains(": ") || value.Contains(" #")
                            || value.Contains("\n") || value.Contains("\\");
            if (needsQuotes)
                return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return value;
        }

        /// <summary>Returns a YAML-safe mapping key (no quoting needed for typical IDs).</summary>
        static string YamlKey(string value)
        {
            if (value == null) return "\"\"";
            return value;
        }

        static string FormatFloat(float v)   => v.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        static string FormatDouble(double v)  => v.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        static string FormatBool(bool v)      => v ? "true" : "false";

        static string FormatNullableFloat(float? v)
        {
            if (v == null) return "null";
            return FormatFloat(v.Value);
        }

        static string FormatNullableDouble(double? v)
        {
            if (v == null) return "null";
            return FormatDouble(v.Value);
        }

        /// <summary>Appends a dict as YAML mapping. Skips empty/null values.</summary>
        static void AppendDict(StringBuilder sb, string keyWithIndent, Dictionary<string, double> dict)
        {
            if (dict == null || dict.Count == 0)
                return;
            sb.AppendLine($"{keyWithIndent}:");
            string indent = keyWithIndent.Length - keyWithIndent.TrimStart().Length == 0
                ? "  "
                : new string(' ', keyWithIndent.Length - keyWithIndent.TrimStart().Length + 2);
            foreach (var kv in dict)
                sb.AppendLine($"{indent}{YamlKey(kv.Key)}: {FormatDouble(kv.Value)}");
        }

        /// <summary>Appends a list of strings as YAML sequence. Skips empty/null values.</summary>
        static void AppendStringList(StringBuilder sb, string keyWithIndent, List<string> list)
        {
            if (list == null || list.Count == 0)
                return;
            sb.AppendLine($"{keyWithIndent}:");
            string indent = keyWithIndent.Length - keyWithIndent.TrimStart().Length == 0
                ? "  "
                : new string(' ', keyWithIndent.Length - keyWithIndent.TrimStart().Length + 2);
            foreach (var item in list)
                sb.AppendLine($"{indent}- {YamlScalar(item)}");
        }

        /// <summary>Appends byproduct entries as YAML sequence. Skips empty/null values.</summary>
        static void AppendByproducts(StringBuilder sb, string keyWithIndent, List<ByproductEntry> list)
        {
            if (list == null || list.Count == 0)
                return;
            sb.AppendLine($"{keyWithIndent}:");
            string indent = keyWithIndent.Length - keyWithIndent.TrimStart().Length == 0
                ? "  "
                : new string(' ', keyWithIndent.Length - keyWithIndent.TrimStart().Length + 2);
            foreach (var bp in list)
            {
                sb.AppendLine($"{indent}- resource: {YamlScalar(bp.Resource)}");
                sb.AppendLine($"{indent}  rate: {FormatDouble(bp.Rate)}");
                sb.AppendLine($"{indent}  state: {YamlScalar(bp.State)}");
            }
        }

        // ── Data classes ──────────────────────────────────────────────────────

        class ByproductEntry
        {
            public string Resource { get; set; }
            public double Rate     { get; set; }
            public string State    { get; set; }
        }
    }
}
