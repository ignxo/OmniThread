using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace OmniThreadUltimate
{
    public static class ModCompatibilityManager
    {
        // Single unified re-entrant lock shared with ParallelTickEngine.GlobalSyncLock
        // Having |L| = 1 lock across all synchronized mod hooks makes AB-BA lock inversion mathematically impossible.
        public static object UnifiedModSyncLock
        {
            get { return ParallelTickEngine.GlobalSyncLock; }
        }

        public static readonly ConcurrentDictionary<Type, bool> TypeMainThreadDecisionCache = new ConcurrentDictionary<Type, bool>();
        public static readonly List<Type> BaseMainThreadTypes = new List<Type>(16);
        public static readonly List<string> ActiveModNames = new List<string>(64);
        public static readonly List<string> AppliedCompatibilityRules = new List<string>(64);

        public static int DetectedActiveModsCount = 0;
        public static int AppliedModCompatPatchesCount = 0;
        public static int AutoSafeModFallbackTypesCount = 0;
        public static long MainThreadRoutedModThingTicks = 0;

        public static bool HasMissileGirl = false;
        public static bool HasCombatAI = false;
        public static bool HasCombatExtended = false;
        public static bool HasVehicleFramework = false;
        public static bool HasZoologyMod = false;
        public static bool HasPrepatcher = false;
        public static bool HasVEF = false;

        private static Harmony StoredHarmony;
        private static bool LateGameplayPatchesApplied = false;
        private static bool ThirdPartyAssembliesScanned = false;
        private static readonly HashSet<MethodBase> PatchedMethods = new HashSet<MethodBase>();
        private static readonly HashSet<MethodBase> SkipPatchedMethods = new HashSet<MethodBase>();

        // CombatAI CompCache Pre-Warming structures
        private static bool CompCacheInitialized = false;
        private static Type[] CAICompTypes;
        private static IDictionary[] CAICompDicts;
        private static MethodInfo[] CAIMapCompGetters;
        private static bool[] PreWarmedThingIdFlags = new bool[262144];
        private static bool[] FaultedThingIdFlags = new bool[262144];
        private static readonly HashSet<int> PreWarmedMapIds = new HashSet<int>();
        private static readonly Dictionary<int, int> LastPreWarmedMapThingCounts = new Dictionary<int, int>(8);

        public static void Initialize(Harmony harmony)
        {
            try
            {
                StoredHarmony = harmony;
                LateGameplayPatchesApplied = false;
                ThirdPartyAssembliesScanned = false;
                TypeMainThreadDecisionCache.Clear();
                BaseMainThreadTypes.Clear();
                ActiveModNames.Clear();
                AppliedCompatibilityRules.Clear();
                PreWarmedMapIds.Clear();
                LastPreWarmedMapThingCounts.Clear();
                Array.Clear(PreWarmedThingIdFlags, 0, PreWarmedThingIdFlags.Length);
                Array.Clear(FaultedThingIdFlags, 0, FaultedThingIdFlags.Length);
                LastPreWarmTailTick = -1;
                AppliedModCompatPatchesCount = 0;

                // Fast-path known safe vanilla concrete types
                TypeMainThreadDecisionCache[typeof(Pawn)] = false;
                TypeMainThreadDecisionCache[typeof(Plant)] = false;
                TypeMainThreadDecisionCache[typeof(Building_Door)] = false;
                TypeMainThreadDecisionCache[typeof(Filth)] = false;

                // Vanilla types that spawn/despawn or shake camera during Tick
                RegisterMainThreadBaseType(typeof(Skyfaller), "Verse.Skyfaller (MainThread Tick Routing)");

                ScanRunningMods();
                ApplyCompatibilityPatches(harmony);

                LongEventHandler.ExecuteWhenFinished(delegate
                {
                    try
                    {
                        if (StoredHarmony != null)
                        {
                            ApplyCompatibilityPatches(StoredHarmony);
                        }
                    }
                    catch
                    {
                    }
                });

                Log.Message(string.Format(
                    "[OmniThread] ModCompatibilityManager initialized: ActiveMods={0}, CompatPatchesApplied={1}, MainThreadBaseTypes={2} (MissileGirl={3}, CAI5000={4}, CE={5}, Vehicles={6}, Zoology={7}, VEF={8}, Prepatcher={9})",
                    DetectedActiveModsCount,
                    AppliedModCompatPatchesCount,
                    BaseMainThreadTypes.Count,
                    HasMissileGirl,
                    HasCombatAI,
                    HasCombatExtended,
                    HasVehicleFramework,
                    HasZoologyMod,
                    HasVEF,
                    HasPrepatcher));
            }
            catch (Exception ex)
            {
                Log.Warning("[OmniThread] ModCompatibilityManager.Initialize warning: " + ex);
            }
        }

        public static void EnsureLateCompatibilityPatchesApplied()
        {
            PreWarmDefTexturesOnMainThread();
            if (LateGameplayPatchesApplied || StoredHarmony == null)
            {
                return;
            }
            LateGameplayPatchesApplied = true;
            try
            {
                int beforeCount = AppliedModCompatPatchesCount;
                ApplyCompatibilityPatches(StoredHarmony);
                int added = AppliedModCompatPatchesCount - beforeCount;
                if (added > 0)
                {
                    Log.Message(string.Format(
                        "[OmniThread] Late post-startup mod compatibility scan protected {0} additional third-party mod methods (Total={1}).",
                        added, AppliedModCompatPatchesCount));
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[OmniThread] EnsureLateCompatibilityPatchesApplied warning: " + ex.Message);
            }
        }

        private static void ScanRunningMods()
        {
            List<ModContentPack> runningMods = LoadedModManager.RunningModsListForReading;
            if (runningMods == null)
            {
                return;
            }

            DetectedActiveModsCount = runningMods.Count;
            for (int i = 0; i < runningMods.Count; i++)
            {
                ModContentPack mod = runningMods[i];
                if (mod == null)
                {
                    continue;
                }

                string pkgId = mod.PackageId != null ? mod.PackageId.ToLowerInvariant() : "";
                string name = mod.Name != null ? mod.Name : pkgId;
                ActiveModNames.Add(string.Format("{0} ({1})", name, pkgId));

                if (pkgId.Contains("missilegirl") || pkgId.Contains("rocketman"))
                {
                    HasMissileGirl = true;
                }
                else if (pkgId.Contains("krkr.rule56") || pkgId.Contains("combatai"))
                {
                    HasCombatAI = true;
                }
                else if (pkgId.Contains("combatextended"))
                {
                    HasCombatExtended = true;
                }
                else if (pkgId.Contains("vehicleframework") || pkgId.Contains("smashphil.vehicle"))
                {
                    HasVehicleFramework = true;
                }
                else if (pkgId.Contains("zoologymod"))
                {
                    HasZoologyMod = true;
                }
                else if (pkgId.Contains("prepatcher"))
                {
                    HasPrepatcher = true;
                }
                else if (pkgId.Contains("oskarpotocki.vanillafactionsexpanded.core"))
                {
                    HasVEF = true;
                }
            }
        }

        private static bool RegisterMainThreadBaseType(Type baseType, string ruleLabel)
        {
            if (baseType == null)
            {
                return false;
            }
            if (BaseMainThreadTypes.Contains(baseType))
            {
                return false;
            }
            BaseMainThreadTypes.Add(baseType);
            TypeMainThreadDecisionCache[baseType] = true;
            if (!string.IsNullOrEmpty(ruleLabel))
            {
                AppliedCompatibilityRules.Add(ruleLabel);
            }
            return true;
        }

        public static void MarkThingFaultedForMainThread(Thing thing)
        {
            if (thing == null)
            {
                return;
            }
            int id = thing.thingIDNumber;
            if (id < 0)
            {
                return;
            }
            bool[] flags = FaultedThingIdFlags;
            if (id < flags.Length && flags[id])
            {
                return;
            }
            lock (ParallelTickEngine.GlobalSyncLock)
            {
                flags = FaultedThingIdFlags;
                if (id >= flags.Length)
                {
                    int newLen = Math.Max(flags.Length * 2, id + 65536);
                    bool[] newFlags = new bool[newLen];
                    Array.Copy(flags, newFlags, flags.Length);
                    FaultedThingIdFlags = newFlags;
                    flags = newFlags;
                }
                if (!flags[id])
                {
                    flags[id] = true;
                    Interlocked.Increment(ref AutoSafeModFallbackTypesCount);
                }
            }
        }

        public static bool ShouldTickOnMainThread(Thing thing, Type thingType)
        {
            if (thing != null)
            {
                int id = thing.thingIDNumber;
                bool[] faulted = FaultedThingIdFlags;
                if ((uint)id < (uint)faulted.Length && faulted[id])
                {
                    return true;
                }
                if (thingType == null)
                {
                    thingType = thing.GetType();
                }
            }

            if (thingType == null)
            {
                return false;
            }

            if (thingType == typeof(Pawn) || thingType == typeof(Plant))
            {
                return false;
            }

            bool decision;
            if (TypeMainThreadDecisionCache.TryGetValue(thingType, out decision))
            {
                return decision;
            }

            decision = ComputeMainThreadDecision(thingType);
            TypeMainThreadDecisionCache[thingType] = decision;
            return decision;
        }

        private static bool ComputeMainThreadDecision(Type thingType)
        {
            for (int i = 0; i < BaseMainThreadTypes.Count; i++)
            {
                Type bt = BaseMainThreadTypes[i];
                if (bt != null && bt.IsAssignableFrom(thingType))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool HandleWorkerThingException(Thing thing, string context, Exception ex)
        {
            if (thing == null)
            {
                return false;
            }

            // Isolate this specific faulted thingIDNumber onto the Main Thread so it stops throwing on worker threads,
            // without ever blacklisting the entire Pawn/Plant class!
            MarkThingFaultedForMainThread(thing);

            Type tType = thing.GetType();
            if (tType != typeof(Pawn) && tType != typeof(Plant) && tType.Assembly != typeof(Thing).Assembly && ex != null)
            {
                string msg = ex.Message ?? string.Empty;
                string stack = ex.StackTrace ?? string.Empty;
                bool isUnityMainThreadError =
                    (ex is UnityEngine.UnityException) ||
                    msg.IndexOf("main thread", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stack.IndexOf("CompareBaseObjectsInternal", StringComparison.Ordinal) >= 0 ||
                    stack.IndexOf("get_transform", StringComparison.Ordinal) >= 0 ||
                    stack.IndexOf("get_gameObject", StringComparison.Ordinal) >= 0;

                if (isUnityMainThreadError)
                {
                    bool alreadyMarked;
                    if (!TypeMainThreadDecisionCache.TryGetValue(tType, out alreadyMarked) || !alreadyMarked)
                    {
                        TypeMainThreadDecisionCache[tType] = true;
                        Interlocked.Increment(ref AutoSafeModFallbackTypesCount);
                    }
                    return true;
                }
            }

            return false;
        }

        public static Type FindModType(string fullTypeName)
        {
            if (string.IsNullOrEmpty(fullTypeName))
            {
                return null;
            }

            List<ModContentPack> runningMods = LoadedModManager.RunningModsListForReading;
            if (runningMods != null)
            {
                for (int i = 0; i < runningMods.Count; i++)
                {
                    ModContentPack mod = runningMods[i];
                    if (mod == null || mod.assemblies == null || mod.assemblies.loadedAssemblies == null)
                    {
                        continue;
                    }
                    List<Assembly> asms = mod.assemblies.loadedAssemblies;
                    for (int j = 0; j < asms.Count; j++)
                    {
                        Assembly asm = asms[j];
                        if (asm == null)
                        {
                            continue;
                        }
                        try
                        {
                            if (asm.ReflectionOnly)
                            {
                                continue;
                            }
                            Type found = asm.GetType(fullTypeName, false, false);
                            if (found != null)
                            {
                                return found;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return AccessTools.TypeByName(fullTypeName);
        }

        private static void InitializeCombatAICompCacheReflection()
        {
            if (CompCacheInitialized)
            {
                return;
            }
            CompCacheInitialized = true;

            try
            {
                Type compCacheType = FindModType("CombatAI.CompCache");
                if (compCacheType == null)
                {
                    return;
                }

                Type openThingCompCache = compCacheType.GetNestedType("ThingComp_Cache`1", BindingFlags.Public | BindingFlags.NonPublic);
                if (openThingCompCache != null)
                {
                    string[] compTypeNames = new string[]
                    {
                        "CombatAI.Comps.ThingComp_CombatAI",
                        "Verse.CompAttachBase",
                        "RimWorld.CompCanBeDormant",
                        "CombatAI.Comps.ThingComp_Sighter",
                        "CombatAI.Comps.ThingComp_CCTVTop",
                        "RimWorld.CompMannable",
                        "RimWorld.CompPowerTrader",
                        "CombatAI.Comps.ThingComp_Statistics"
                    };

                    List<Type> validTypes = new List<Type>(8);
                    List<IDictionary> validDicts = new List<IDictionary>(8);

                    for (int i = 0; i < compTypeNames.Length; i++)
                    {
                        Type ct = FindModType(compTypeNames[i]);
                        if (ct != null && typeof(ThingComp).IsAssignableFrom(ct))
                        {
                            Type closedCache = openThingCompCache.MakeGenericType(ct);
                            FieldInfo dictField = closedCache.GetField("compsById", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            if (dictField != null)
                            {
                                IDictionary dict = dictField.GetValue(null) as IDictionary;
                                if (dict != null)
                                {
                                    validTypes.Add(ct);
                                    validDicts.Add(dict);
                                }
                            }
                        }
                    }

                    CAICompTypes = validTypes.ToArray();
                    CAICompDicts = validDicts.ToArray();
                }

                // Find generic GetComp_Fast<T>(this Map map)
                MethodInfo openMapGetComp = null;
                MethodInfo[] methods = compCacheType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m.Name == "GetComp_Fast" && m.IsGenericMethodDefinition)
                    {
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType == typeof(Map))
                        {
                            openMapGetComp = m;
                            break;
                        }
                    }
                }

                if (openMapGetComp != null)
                {
                    string[] mapCompNames = new string[]
                    {
                        "CombatAI.AvoidanceTracker",
                        "CombatAI.MapComponent_CombatAI",
                        "CombatAI.MapComponent_FogGrid",
                        "CombatAI.RoofGrid",
                        "CombatAI.SightTracker",
                        "CombatAI.TurretTracker",
                        "CombatAI.WallCCTVTracker",
                        "CombatAI.WallGrid"
                    };

                    List<MethodInfo> getters = new List<MethodInfo>(8);
                    for (int i = 0; i < mapCompNames.Length; i++)
                    {
                        Type mct = FindModType(mapCompNames[i]);
                        if (mct != null && typeof(MapComponent).IsAssignableFrom(mct))
                        {
                            getters.Add(openMapGetComp.MakeGenericMethod(mct));
                        }
                    }
                    CAIMapCompGetters = getters.ToArray();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[OmniThread] InitializeCombatAICompCacheReflection warning: " + ex.Message);
            }
        }

        private static int LastPreWarmTailTick = -1;
        private static bool DefTexturesPreWarmed = false;

        public static void PreWarmDefTexturesOnMainThread()
        {
            if (DefTexturesPreWarmed)
            {
                return;
            }
            DefTexturesPreWarmed = true;
            try
            {
                List<AbilityDef> abilities = DefDatabase<AbilityDef>.AllDefsListForReading;
                if (abilities != null)
                {
                    for (int i = 0; i < abilities.Count; i++)
                    {
                        AbilityDef def = abilities[i];
                        if (def != null)
                        {
                            try { object tex = def.WarmupMoteSocialSymbol; } catch { }
                        }
                    }
                }
                List<InteractionDef> interactions = DefDatabase<InteractionDef>.AllDefsListForReading;
                if (interactions != null)
                {
                    for (int i = 0; i < interactions.Count; i++)
                    {
                        InteractionDef def = interactions[i];
                        if (def != null)
                        {
                            try { object tex = def.GetSymbol(null, null); } catch { }
                        }
                    }
                }
                List<PreceptDef> precepts = DefDatabase<PreceptDef>.AllDefsListForReading;
                if (precepts != null)
                {
                    for (int i = 0; i < precepts.Count; i++)
                    {
                        PreceptDef def = precepts[i];
                        if (def != null)
                        {
                            try { object tex = def.Icon; } catch { }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        public static void PreWarmBeforeParallelTick(List<Thing> currentBucket)
        {
            PreWarmDefTexturesOnMainThread();

            if (!HasCombatAI)
            {
                return;
            }

            if (!CompCacheInitialized)
            {
                InitializeCombatAICompCacheReflection();
            }

            if (CAICompTypes == null || CAICompDicts == null || CAICompTypes.Length == 0)
            {
                return;
            }

            try
            {
                int ticksGame = Find.TickManager != null ? Find.TickManager.TicksGame : -1;
                bool scanTailThisTick = false;
                if (ticksGame != LastPreWarmTailTick)
                {
                    LastPreWarmTailTick = ticksGame;
                    scanTailThisTick = true;
                }

                List<Map> maps = Find.Maps;
                if (maps != null)
                {
                    object[] singleArg = null;
                    for (int m = 0; m < maps.Count; m++)
                    {
                        Map map = maps[m];
                        if (map == null)
                        {
                            continue;
                        }

                        bool isNewMap = PreWarmedMapIds.Add(map.uniqueID);
                        if (isNewMap && CAIMapCompGetters != null)
                        {
                            if (singleArg == null)
                            {
                                singleArg = new object[1];
                            }
                            singleArg[0] = map;
                            for (int g = 0; g < CAIMapCompGetters.Length; g++)
                            {
                                try
                                {
                                    CAIMapCompGetters[g].Invoke(null, singleArg);
                                }
                                catch
                                {
                                }
                            }
                        }

                        if (map.listerThings != null && map.listerThings.AllThings != null)
                        {
                            List<Thing> allThings = map.listerThings.AllThings;
                            int allCount = allThings.Count;
                            if (isNewMap)
                            {
                                PreWarmThingList(allThings, 0, true);
                                LastPreWarmedMapThingCounts[map.uniqueID] = allCount;
                            }
                            else if (scanTailThisTick)
                            {
                                int lastCount;
                                if (!LastPreWarmedMapThingCounts.TryGetValue(map.uniqueID, out lastCount))
                                {
                                    lastCount = 0;
                                }
                                if (allCount != lastCount)
                                {
                                    int startIdx = Math.Max(0, Math.Min(lastCount - 8, allCount - 64));
                                    PreWarmThingList(allThings, startIdx, true);
                                    LastPreWarmedMapThingCounts[map.uniqueID] = allCount;
                                }
                            }
                        }
                    }
                }

                if (currentBucket != null && (ticksGame & 255) == 0)
                {
                    PreWarmThingList(currentBucket, 0, false);
                }
            }
            catch
            {
            }
        }

        private static void PreWarmThingList(List<Thing> things, int startIndex, bool forcePawnGear)
        {
            int count = things.Count;
            for (int i = startIndex; i < count; i++)
            {
                Thing t = things[i];
                ThingWithComps twc = t as ThingWithComps;
                if (twc == null)
                {
                    continue;
                }

                bool newlyWarmed = PreWarmSingleThingWithComps(twc);

                Pawn pawn = twc as Pawn;
                if (pawn != null)
                {
                    if (newlyWarmed || forcePawnGear)
                    {
                        if (pawn.equipment != null)
                        {
                            List<ThingWithComps> eqList = pawn.equipment.AllEquipmentListForReading;
                            if (eqList != null)
                            {
                                for (int e = 0; e < eqList.Count; e++)
                                {
                                    PreWarmSingleThingWithComps(eqList[e]);
                                }
                            }
                        }

                        if (pawn.apparel != null)
                        {
                            List<Apparel> appList = pawn.apparel.WornApparel;
                            if (appList != null)
                            {
                                for (int a = 0; a < appList.Count; a++)
                                {
                                    PreWarmSingleThingWithComps(appList[a]);
                                }
                            }
                        }
                    }

                    if (pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null)
                    {
                        PreWarmSingleThingWithComps(pawn.carryTracker.CarriedThing as ThingWithComps);
                    }
                }
            }
        }

        private static bool PreWarmSingleThingWithComps(ThingWithComps twc)
        {
            if (twc == null)
            {
                return false;
            }

            int id = twc.thingIDNumber;
            if (id < 0)
            {
                return false;
            }

            bool[] flags = PreWarmedThingIdFlags;
            if (id < flags.Length && flags[id])
            {
                return false;
            }
            lock (ParallelTickEngine.GlobalSyncLock)
            {
                flags = PreWarmedThingIdFlags;
                if (id >= flags.Length)
                {
                    int newLen = Math.Max(flags.Length * 2, id + 65536);
                    bool[] newFlags = new bool[newLen];
                    Array.Copy(flags, newFlags, flags.Length);
                    PreWarmedThingIdFlags = newFlags;
                    flags = newFlags;
                }
                if (flags[id])
                {
                    return false;
                }
                flags[id] = true;
            }

            int compTypeCount = CAICompTypes.Length;
            List<ThingComp> allComps = twc.AllComps;
            object boxedId = id;

            for (int c = 0; c < compTypeCount; c++)
            {
                Type targetType = CAICompTypes[c];
                ThingComp foundComp = null;

                if (allComps != null)
                {
                    for (int k = 0; k < allComps.Count; k++)
                    {
                        ThingComp comp = allComps[k];
                        if (comp != null && targetType.IsAssignableFrom(comp.GetType()))
                        {
                            foundComp = comp;
                            break;
                        }
                    }
                }

                // Storing either the component instance OR null guarantees TryGetValue(id, out val) returns true!
                CAICompDicts[c][boxedId] = foundComp;
            }
            return true;
        }

        private static void ApplyCompatibilityPatches(Harmony harmony)
        {
            // 1. Vehicle Framework (3014915404) - Route multi-cell VehiclePawn to main thread
            Type vehiclePawnType = FindModType("Vehicles.VehiclePawn");
            if (vehiclePawnType != null)
            {
                HasVehicleFramework = true;
                if (RegisterMainThreadBaseType(vehiclePawnType, "VehicleFramework: Vehicles.VehiclePawn -> MainThread"))
                {
                    AppliedModCompatPatchesCount++;
                }
            }

            // 2. Combat Extended (ceteam.combatextended) - Route ballistic ProjectileCE & lock LightingTracker
            Type projectileCEType = FindModType("CombatExtended.ProjectileCE");
            if (projectileCEType != null)
            {
                HasCombatExtended = true;
                if (RegisterMainThreadBaseType(projectileCEType, "CombatExtended: ProjectileCE -> MainThread"))
                {
                    AppliedModCompatPatchesCount++;
                }
            }

            Type ceLightingTrackerType = FindModType("CombatExtended.LightingTracker");
            if (ceLightingTrackerType != null)
            {
                HasCombatExtended = true;
                PatchAllOverloadsWithUnifiedLock(harmony, ceLightingTrackerType, "GetGlowForCell", "CombatExtended: LightingTracker.GetGlowForCell");
                PatchAllOverloadsWithUnifiedLock(harmony, ceLightingTrackerType, "CombatGlowAt", "CombatExtended: LightingTracker.CombatGlowAt");
                PatchAllOverloadsWithUnifiedLock(harmony, ceLightingTrackerType, "CombatGlowAtFor", "CombatExtended: LightingTracker.CombatGlowAtFor");
                PatchAllOverloadsWithUnifiedLock(harmony, ceLightingTrackerType, "Notify_ShotsFiredAt", "CombatExtended: LightingTracker.Notify_ShotsFiredAt");
            }

            Type ceModifyWeaponType = FindModType("CombatExtended.JobGiver_ModifyCarriedWeapon");
            if (ceModifyWeaponType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, ceModifyWeaponType, "TryGiveJob", "CombatExtended: JobGiver_ModifyCarriedWeapon.TryGiveJob");
            }

            Type ceUpdateLoadoutType = FindModType("CombatExtended.JobGiver_UpdateLoadout");
            if (ceUpdateLoadoutType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, ceUpdateLoadoutType, "TryGiveJob", "CombatExtended: JobGiver_UpdateLoadout.TryGiveJob");
            }

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatExtended.Verb_LaunchProjectileCE", new string[]
            {
                "TryFindCEShootLineFromTo", "CanHitTargetFrom", "CanHitCellFromCellIgnoringRange",
                "CanHitFromCellIgnoringRange", "AdjustShotHeight", "GetHighestCoverAndSmokeForTarget",
                "get_ShotHeight", "TryStartCastOn", "VerbTickCE", "TryCastShot"
            });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatExtended.Verb_ShootCE", new string[]
            {
                "CanHitTargetFrom", "TryCastShot", "VerbTickCE"
            });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatExtended.CollisionVertical", new string[]
            {
                "CalculateHeightRange"
            });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatExtended.CE_Utility", new string[]
            {
                "GetBoundsFor", "IntersectionPoint", "MaxProjectileRange"
            });

            // 3. Missile Girl / RocketMan 1.6 (vr.missilegirl - 3712928623) - Lock-Free Parallel Bypasses
            Type mgStatWorkerPatchType = FindModType("MissileGirl.Optimizations.StatWorker_Patch");
            if (mgStatWorkerPatchType != null)
            {
                HasMissileGirl = true;
                PatchCustomPrefix(harmony, mgStatWorkerPatchType, "Replacemant", "BypassMissileGirlStatWorkerInParallel", "MissileGirl: StatWorker_Patch.Replacemant (Lock-Free Parallel Bypass)");
            }

            Type mgSignatureUtilType = FindModType("MissileGirl.SignatureUtility");
            if (mgSignatureUtilType != null)
            {
                HasMissileGirl = true;
                PatchCustomPrefix(harmony, mgSignatureUtilType, "GetSignature", "BypassMissileGirlSignatureInParallel", "MissileGirl: SignatureUtility.GetSignature (Lock-Free Parallel Bypass)");
            }

            Type mgGenTempPatchType = FindModType("MissileGirl.Optimizations.GenTemperature_Patch");
            if (mgGenTempPatchType != null)
            {
                HasMissileGirl = true;
                PatchCustomPrefix(harmony, mgGenTempPatchType, "Prefix", "BypassMissileGirlGenTempPrefixInParallel", "MissileGirl: GenTemperature_Patch.Prefix (Lock-Free Parallel Bypass)");
            }

            Type mgBeautyNeedsPatchType = FindModType("MissileGirl.Patches.BeautyUtility_Patch+Pawn_NeedsTracker_Patch");
            if (mgBeautyNeedsPatchType != null)
            {
                HasMissileGirl = true;
                PatchMethodSkipInParallel(harmony, mgBeautyNeedsPatchType, "Prefix", "MissileGirl: BeautyUtility_Patch.Pawn_NeedsTracker_Patch.Prefix (Skip Static curPawn in Parallel)");
            }

            // 4. CAI 5000 / CombatAI (krkr.rule56 - 3673768803)
            Type caiStatCacheType = FindModType("CombatAI.StatCache");
            if (caiStatCacheType != null)
            {
                HasCombatAI = true;
                InitializeCombatAICompCacheReflection();
                PatchCustomPrefix(harmony, caiStatCacheType, "GetStatValue_Fast", "BypassCombatAIStatCacheInParallel", "CombatAI: StatCache.GetStatValue_Fast (Lock-Free Parallel Bypass)");
            }

            Type caiCompCacheType = FindModType("CombatAI.CompCache");
            if (caiCompCacheType != null)
            {
                HasCombatAI = true;
                PatchCustomPrefix(harmony, caiCompCacheType, "Notify_ThingDestroyed", "CombatAINotifyThingDestroyedPrefix", "CombatAI: CompCache.Notify_ThingDestroyed (Null-Keep Cache Entry)");
            }

            Type caiGenSight1Type = FindModType("CombatAI.Patches.GenSight_Patch+GenSight_LineOfSight1_Patch");
            if (caiGenSight1Type != null)
            {
                HasCombatAI = true;
                PatchMethodSkipInParallel(harmony, caiGenSight1Type, "Prefix", "CombatAI: GenSight_LineOfSight1_Patch.Prefix (Skip Static Mutation in Parallel)");
            }

            Type caiGenSight2Type = FindModType("CombatAI.Patches.GenSight_Patch+GenSight_LineOfSight2_Patch");
            if (caiGenSight2Type != null)
            {
                HasCombatAI = true;
                PatchMethodSkipInParallel(harmony, caiGenSight2Type, "Prefix", "CombatAI: GenSight_LineOfSight2_Patch.Prefix (Skip Static Mutation in Parallel)");
            }

            // CombatAI Flooders & Tactical Cover Finders (Unified Re-Entrant Lock)
            Type caiRegionFlooderType = FindModType("CombatAI.RegionFlooder");
            if (caiRegionFlooderType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, caiRegionFlooderType, "Flood", "CombatAI: RegionFlooder.Flood (Unified Lock)");
            }

            Type caiCellFlooderType = FindModType("CombatAI.CellFlooder");
            if (caiCellFlooderType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, caiCellFlooderType, "Flood", "CombatAI: CellFlooder.Flood (Unified Lock)");
            }

            Type caiCoverPosFinderType = FindModType("CombatAI.CoverPositionFinder");
            if (caiCoverPosFinderType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, caiCoverPosFinderType, "TryFindCoverPosition", "CombatAI: CoverPositionFinder.TryFindCoverPosition");
                PatchAllOverloadsWithUnifiedLock(harmony, caiCoverPosFinderType, "TryFindRetreatPosition", "CombatAI: CoverPositionFinder.TryFindRetreatPosition");
                PatchAllOverloadsWithUnifiedLock(harmony, caiCoverPosFinderType, "TryFindDuckPosition", "CombatAI: CoverPositionFinder.TryFindDuckPosition");
            }

            Type caiCastPosType = FindModType("CombatAI.Patches.Harmony_CastPositionFinder");
            if (caiCastPosType != null)
            {
                HasCombatAI = true;
                PatchAllOverloadsWithUnifiedLock(harmony, typeof(JobGiver_AIFightEnemies), "TryFindShootingPosition", "CombatAI: JobGiver_AIFightEnemies.TryFindShootingPosition");
                PatchAllOverloadsWithUnifiedLock(harmony, typeof(CastPositionFinder), "TryFindCastPosition", "CombatAI: CastPositionFinder.TryFindCastPosition");
                PatchAllOverloadsWithUnifiedLock(harmony, typeof(CastPositionFinder), "CastPositionPreference", "CombatAI: CastPositionFinder.CastPositionPreference");
            }

            Type caiGotoHostileType = FindModType("CombatAI.Patches.JobGiver_AIGotoNearestHostile_Patch+JobGiver_AIGotoNearestHostile_TryGiveJob_Patch");
            if (caiGotoHostileType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, caiGotoHostileType, "Prefix", "CombatAI: JobGiver_AIGotoNearestHostile_Patch.Prefix");
            }

            Type caiTrashBuildingsType = FindModType("CombatAI.Patches.JobGiver_AITrashBuildingsDistant_Patch+JobGiver_AITrashBuildingsDistant_TryGiveJob_Patch");
            if (caiTrashBuildingsType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, caiTrashBuildingsType, "Prefix", "CombatAI: JobGiver_AITrashBuildingsDistant_Patch.Prefix");
            }

            Type caiThinkNodePatchType = FindModType("CombatAI.Patches.ThinkNode_Patch+ThinkNode_TryIssueJobPackage_Patch");
            if (caiThinkNodePatchType != null)
            {
                PatchMethodSkipInParallel(harmony, caiThinkNodePatchType, "Postfix", "CombatAI: ThinkNode_TryIssueJobPackage_Patch.Postfix (Skip Debug Logger in Parallel)");
            }

            Type caiVerbStartCastType = FindModType("CombatAI.Patches.Verb_Patch+Verb_TryStartCast_Patch");
            if (caiVerbStartCastType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, caiVerbStartCastType, "Prefix", "CombatAI: Verb_TryStartCast_Patch.Prefix");
                PatchAllOverloadsWithUnifiedLock(harmony, caiVerbStartCastType, "Postfix", "CombatAI: Verb_TryStartCast_Patch.Postfix");
            }

            Type caiVerbBurstShotType = FindModType("CombatAI.Patches.Verb_Patch+Verb_TryCastNextBurstShot_Patch");
            if (caiVerbBurstShotType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, caiVerbBurstShotType, "OnShot", "CombatAI: Verb_TryCastNextBurstShot_Patch.OnShot");
            }

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.WallGrid", new string[] { "RecalculateCell" });

            Type caiDoorClearCacheType = FindModType("CombatAI.Patches.Building_Door_Patch+Building_Door_ClearReachabilityCache_Patch");
            if (caiDoorClearCacheType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, caiDoorClearCacheType, "Prefix", "CombatAI: Building_Door_ClearReachabilityCache_Patch.Prefix");
            }

            Type caiFactionTookDmgType = FindModType("CombatAI.Patches.Faction_Patch+Faction_Notify_MemberTookDamage_Patch");
            if (caiFactionTookDmgType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, caiFactionTookDmgType, "Prefix", "CombatAI: Faction_Notify_MemberTookDamage_Patch.Prefix");
            }

            Type caiFactionDiedType = FindModType("CombatAI.Patches.Faction_Patch+Faction_Notify_MemberDied_Patch");
            if (caiFactionDiedType != null)
            {
                PatchAllOverloadsWithUnifiedLock(harmony, caiFactionDiedType, "Prefix", "CombatAI: Faction_Notify_MemberDied_Patch.Prefix");
            }

            // 0. Core RimWorld reflection / floodfiller helpers triggered concurrently by mods (CombatExtended, CombatAI)
            PatchAllOverloadsWithLeafLock(harmony, typeof(GenTypes), "AllSubclasses", "Verse: GenTypes.AllSubclasses (Leaf Cache Lock)");
            PatchAllOverloadsWithLeafLock(harmony, typeof(GenTypes), "AllSubclassesNonAbstract", "Verse: GenTypes.AllSubclassesNonAbstract (Leaf Cache Lock)");
            PatchAllOverloadsWithUnifiedLock(harmony, typeof(FloodFiller), "FloodFill", "Verse: FloodFiller.FloodFill (Unified Lock)");

            // CombatAI TKCache / TKVCache / Dictionary / HashSet mutating methods
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.CombatAI_Utility", new string[]
            {
                "IsDormant", "GetCombatPersonality", "TryGetNextDutyDest",
                "TryGetAvoidanceReader", "TryGetSightReader", "IsApproachingMeleeTarget",
                "TryGetAttackVerb", "GetMovingShiftedPosition", "GetFloatGrid", "GetCellFlooder"
            });

            PatchTypeMethodsWithLeafLock(harmony, "CombatAI.PersonalityTacker", new string[]
            {
                "GetPersonality", "GetPawnPersonality", "GetFactionPersonality"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.ThreatUtility", new string[]
            {
                "CalculateThreat", "GetEnemyAttackTargetId"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.SightUtility", new string[]
            {
                "GetSightRadius_Fast", "GetSightRadius", "GetSightRadius_Pawn",
                "GetSightRadius_Turret", "GetSightRadius_Sighter", "GetFogRadius"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.DamageUtility", new string[]
            {
                "GetDamageReport", "ThreatTo", "GetDamageDef", "Invalidate", "ClearCache"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.DamageReport", new string[]
            {
                "AddVerb", "AddTool", "Finalize"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.ArmorUtility", new string[]
            {
                "GetArmorReport", "CreateReport", "FillApparel", "GetBodyApparels", "Invalidate", "ClearCache"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.PawnBodyModel", new string[]
            {
                "Coverage", "Release"
            });

            PatchTypeMethodsWithLeafLock(harmony, "CombatAI.MetaCombatAttributeUtility", new string[]
            {
                "GetWeaknessAttributes", "GetStrongAttributes", "ClearCache"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.HeuristicDistanceUtility", new string[]
            {
                "HeuristicDistanceTo_RegionWise", "HeuristicDistanceTo",
                "Validator_CellWise_Delegate", "DistanceTo_RegionWise_Delegate"
            });

            PatchTypeMethodsWithLeafLock(harmony, "CombatAI.Settings", new string[]
            {
                "GetDefKindSettings", "GetTechSettings"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.SuppliesUtility", new string[]
            {
                "DropSupplies", "PotentialFoodFor", "FulfillFoodSupplies"
            });

            PatchTypeMethodsWithLeafLock(harmony, "CombatAI.ThinkNodeDatabase", new string[]
            {
                "GetTrace", "BuildNodeTree", "GetTraceMessage"
            });

            PatchTypeMethodsWithLeafLock(harmony, "CombatAI.ThinkNode_Timed", new string[] { "Satisfied" });
            PatchTypeMethodsWithLeafLock(harmony, "CombatAI.ThinkNode_ConditionalBeingTargeted", new string[] { "Satisfied" });
            PatchTypeMethodsWithLeafLock(harmony, "CombatAI.ThinkNode_ConditionalReacted", new string[] { "Satisfied" });
            PatchTypeMethodsWithLeafLock(harmony, "CombatAI.ThinkNode_ConditionalSafe", new string[] { "Satisfied" });
            PatchTypeMethodsWithLeafLock(harmony, "CombatAI.ThinkNode_ConditionalSight", new string[] { "Satisfied" });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.JobGiver_CAIFollowEscortee", new string[] { "TryGiveJob" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.JobGiver_CAIWanderNearEscortee", new string[] { "TryGiveJob" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.JobGiver_DuckOrRetreat", new string[] { "TryGiveJob" });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.CustomDutyUtility", new string[]
            {
                "GetPawnCustomDutyTracker", "AnyCustomDuties", "TryStartCustomDuty",
                "EnqueueFirstCustomDuty", "EnqueueCustomDuty", "Escort", "AssaultPoint",
                "DefendPoint", "HuntDownEnemies"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.PawnPathUtility", new string[]
            {
                "GetMovingShiftedPosition", "TryGetCellIndexAhead", "TryGetSapperSubPath", "WalkableBy"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.AvoidanceTracker", new string[]
            {
                "Notify_Injury", "Notify_Death", "Notify_PathFound", "Notify_CoverPositionSelected",
                "TryCastProximity", "TryCastPath", "TryCastShootLine"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.MapComponent_FogGrid", new string[] { "RevealSpot" });

            // Only lock cross-pawn notifications / lifecycle on ThingComp_CombatAI so CompTickRare, ShouldMoveTo, and ShouldShootNow run in parallel across 12 threads
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Comps.ThingComp_CombatAI", new string[]
            {
                "PostDeSpawn", "PostSpawnSetup", "OnScanCompleted",
                "Notify_TookDamage", "Notify_BulletImpact",
                "StartAggroCountdown", "TryAggro", "StartSapper", "ReleaseEscorts",
                "Notify_BeingTargeted", "Notify_EnemiesVisible", "Notify_Enemy", "Notify_Ally",
                "Notify_WaitJobEnded", "Notify_SightReaderChanged",
                "TryStartSapperJob", "TryStartSapperJobForEscort", "TryStartRemoteSapper"
            });

            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.Pawn_JobTracker_Patch+Pawn_JobTracker_TryTakeOrderedJob_Patch", new string[] { "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.Pawn_JobTracker_Patch+Pawn_StartJob_Patch", new string[] { "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.JobDriver_Wait_Patch+JobDriver_Wait_MakeNewToils_Patch+<>c__DisplayClass0_0", new string[] { "<Postfix>b__0" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.JobGiver_AITrashBuildingsDistant_Patch+JobGiver_AITrashBuildingsDistant_TryGiveJob_Patch", new string[] { "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.LordToil_AssaultColony_Patch", new string[] { "GetZoneTotalMarketValue" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.LordToil_AssaultColony_Patch+LordToil_AssaultColony_UpdateAllDuties_Patch", new string[] { "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.InterceptorTracker", new string[] { "TryRegister" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.Pawn_ApparelTracker_Patch+Pawn_ApparelTracker_Notify_ApparelAdded_Patch", new string[] { "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.Pawn_ApparelTracker_Patch+Pawn_ApparelTracker_Notify_ApparelRemoved_Patch", new string[] { "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.Pawn_EquipmentTracker_Patch+Pawn_EquipmentTracker_Notify_EquipmentAdded_Patch", new string[] { "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.Pawn_EquipmentTracker_Patch+Pawn_EquipmentTracker_Notify_EquipmentRemoved_Patch", new string[] { "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.ThingWithComps_Patch+ThingWithComps_Destroy_Patch", new string[] { "Prefix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CombatAI.Patches.Thing_Patch+Thing_DeSpawn_Patch", new string[] { "Prefix" });

            // 5. RunAndGun (roolo.runandgun)
            PatchTypeMethodsWithUnifiedLock(harmony, "RunAndGun.Harmony.JobDriver_SetupToils", new string[] { "checkForAutoAttack" });
            PatchTypeMethodsWithUnifiedLock(harmony, "RunAndGun.Harmony.Verb_TryCastNextBurstShot", new string[] { "SetStanceRunAndGun" });
            PatchTypeMethodsWithUnifiedLock(harmony, "RunAndGun.Harmony.Verb_TryStartCastOn", new string[] { "Prefix" });
            PatchTypeMethodsWithLeafLock(harmony, "RunAndGun.Harmony.Pawn_TicksPerMove", new string[] { "hasLightWeapon" });
            PatchTypeMethodsWithUnifiedLock(harmony, "RunAndGun.CompRunAndGun", new string[] { "CompTickRare", "Initialize" });
            PatchTypeMethodsWithUnifiedLock(harmony, "RunAndGun.Harmony.MentalStateHandler_TryStartMentalState", new string[] { "Postfix" });

            // 6. Zoology Mod (com.abobashark.zoologymod - 3679396881)
            Type zoologyHerdHuntType = FindModType("ZoologyMod.HerdPredatorHuntPatch");
            if (zoologyHerdHuntType != null)
            {
                HasZoologyMod = true;
                PatchAllOverloadsWithUnifiedLock(harmony, zoologyHerdHuntType, "Postfix", "ZoologyMod: HerdPredatorHuntPatch.Postfix");
                PatchAllOverloadsWithUnifiedLock(harmony, zoologyHerdHuntType, "HasPackSupport", "ZoologyMod: HerdPredatorHuntPatch.HasPackSupport");
                PatchAllOverloadsWithUnifiedLock(harmony, zoologyHerdHuntType, "GetHerdCandidates", "ZoologyMod: HerdPredatorHuntPatch.GetHerdCandidates");
                ProtectZoologyModAssembly(harmony, zoologyHerdHuntType.Assembly);
            }

            Type zoologyProtectPreyType = FindModType("ZoologyMod.ProtectPreyMapCache");
            if (zoologyProtectPreyType != null)
            {
                HasZoologyMod = true;
                PatchAllOverloadsWithUnifiedLock(harmony, zoologyProtectPreyType, "Get", "ZoologyMod: ProtectPreyMapCache.Get");
            }

            Type zoologySmallPetType = FindModType("ZoologyMod.Patch_SmallPetThreatDisabled");
            if (zoologySmallPetType != null)
            {
                HasZoologyMod = true;
                PatchAllOverloadsWithUnifiedLock(harmony, zoologySmallPetType, "EnsureSmallPetCandidateSetCached", "ZoologyMod: Patch_SmallPetThreatDisabled.EnsureSmallPetCandidateSetCached");
                PatchAllOverloadsWithUnifiedLock(harmony, zoologySmallPetType, "ShouldDisableThreatForSmallPet", "ZoologyMod: Patch_SmallPetThreatDisabled.ShouldDisableThreatForSmallPet");
                PatchAllOverloadsWithUnifiedLock(harmony, zoologySmallPetType, "IsHostileToPlayerFactionSafe", "ZoologyMod: Patch_SmallPetThreatDisabled.IsHostileToPlayerFactionSafe");
                PatchAllOverloadsWithUnifiedLock(harmony, zoologySmallPetType, "UpdateCachedMembership", "ZoologyMod: Patch_SmallPetThreatDisabled.UpdateCachedMembership");
                PatchAllOverloadsWithUnifiedLock(harmony, zoologySmallPetType, "RemoveCachedMembership", "ZoologyMod: Patch_SmallPetThreatDisabled.RemoveCachedMembership");
                PatchAllOverloadsWithUnifiedLock(harmony, zoologySmallPetType, "InvalidateHostilityCache", "ZoologyMod: Patch_SmallPetThreatDisabled.InvalidateHostilityCache");
                PatchAllOverloadsWithUnifiedLock(harmony, zoologySmallPetType, "RebuildSmallPetCandidateSet", "ZoologyMod: Patch_SmallPetThreatDisabled.RebuildSmallPetCandidateSet");
            }

            string[] zoologyStaticJobGivers = new string[]
            {
                "ZoologyMod.JobGiver_AnimalFlee_Zoology",
                "ZoologyMod.JobGiver_AnimalLickWounds",
                "ZoologyMod.JobGiver_MotherRespondToSuckleRequests",
                "ZoologyMod.JobGiver_YoungSuckleFromMother",
                "ZoologyMod.JobGiver_WanderNearPrey",
                "ZoologyMod.JobGiver_WildCompatibleMate",
                "ZoologyMod.JobGiver_IncubateEggClutch",
                "ZoologyMod.JobGiver_WanderNearEggClutch",
                "ZoologyMod.JobGiver_WanderNearMother",
                "ZoologyMod.JobGiver_AnimalAutoFeed"
            };
            for (int i = 0; i < zoologyStaticJobGivers.Length; i++)
            {
                Type zjg = FindModType(zoologyStaticJobGivers[i]);
                if (zjg != null)
                {
                    PatchAllOverloadsWithUnifiedLock(harmony, zjg, "TryGiveJob", "ZoologyMod: " + zjg.Name + ".TryGiveJob");
                }
            }

            PatchTypeMethodsWithUnifiedLock(harmony, "ZoologyMod.Patch_ScavengeringAI+Inner_FoodUtility_BestFoodSourceOnMap", new string[] { "Postfix", "TryConsumeMapScanBudget" });
            PatchTypeMethodsWithUnifiedLock(harmony, "ZoologyMod.Patch_ScavengeringCore", new string[] { "TryGetScavengerCorpseState", "EnsureScavengerCorpseStateCacheFresh", "TryConsumeAdjacentFallbackBudget", "EnsureRuntimeState" });
            PatchTypeMethodsWithUnifiedLock(harmony, "ZoologyMod.Patch_AnimalFleeFromPredators", new string[]
            {
                "ApplyZoologyFleeLogic", "TryBuildImmediateDangerFleeJob",
                "TryBuildPredatorLoiterFleeJob", "TryBuildHostileThreatFleeJob", "GetCloseMeleeThreatTarget"
            });
            PatchTypeMethodsWithUnifiedLock(harmony, "ZoologyMod.Patch_AnimalFleeFromPredators+ThreatMapCache", new string[] { "TryGetFresh", "TryGetLastKnown", "GetOrRefresh", "AddThreat", "MarkCloseMeleeThreat", "CleanupStaleMaps", "ReturnBucketLists" });
            PatchTypeMethodsWithUnifiedLock(harmony, "ZoologyMod.Patch_ReactToCloseMeleeThreat_Fallback", new string[] { "Prefix", "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "ZoologyMod.Comp_AnimalClotting", new string[] { "CompTickRare" });
            PatchTypeMethodsWithUnifiedLock(harmony, "ZoologyMod.Comp_AnimalRegeneration", new string[] { "CompTickRare" });
            PatchTypeMethodsWithUnifiedLock(harmony, "ZoologyMod.Comp_Ageless", new string[] { "CompTickRare" });
            PatchTypeMethodsWithUnifiedLock(harmony, "ZoologyMod.Comp_DrugsImmune", new string[] { "CompTickRare" });

            // 7. Vanilla Social Interactions Expanded (VSIE)
            PatchTypeMethodsWithUnifiedLock(harmony, "VanillaSocialInteractionsExpanded.JobGiver_ViewingArtTogether", new string[] { "TryGiveJob" });
            PatchTypeMethodsWithUnifiedLock(harmony, "VanillaSocialInteractionsExpanded.JobGiver_HaveMealTogether", new string[] { "TryGiveJob" });
            PatchTypeMethodsWithUnifiedLock(harmony, "VanillaSocialInteractionsExpanded.ImmunityTick_Patch", new string[] { "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "VanillaSocialInteractionsExpanded.BirthdayBiological_Patch", new string[] { "OldAgeBirthday" });
            PatchTypeMethodsWithUnifiedLock(harmony, "VanillaSocialInteractionsExpanded.IsOtherPawnSocialFightingWithMe_Patch", new string[] { "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "VanillaSocialInteractionsExpanded.InteractionWorker_Discord", new string[] { "RandomSelectionWeight" });
            PatchTypeMethodsWithUnifiedLock(harmony, "VanillaSocialInteractionsExpanded.InteractionWorker_Teaching", new string[] { "RandomSelectionWeight", "Interacted" });
            PatchTypeMethodsWithUnifiedLock(harmony, "VanillaSocialInteractionsExpanded.VSIE_Utils", new string[] { "GetAverageOpinionOf", "HaveNoticedTale", "TryRegisterNewColonist" });
            PatchTypeMethodsWithUnifiedLock(harmony, "VanillaSocialInteractionsExpanded.SocialInteractionsManager", new string[] { "Notify_AspirationProgress", "TryRegisterNewIspiration", "Notify_InspirationExpired", "TryDevelopNewTrait", "RemoveDestroyedPawn" });

            // 8. Vanilla Memes Expanded (VME)
            PatchTypeMethodsWithUnifiedLock(harmony, "VanillaMemesExpanded.PawnCollectionClass", new string[]
            {
                "AddColonistToIllnessList", "IncreasePawnIllnessTicks", "ResetPawnIlnessTicks",
                "AddColonistToCaravanList", "IncreasePawnCaravanTicks", "ResetPawnCaravanTicks",
                "AddColonistToScarList", "SetPawnScars", "AddColonistToJunkList", "SetPawnJunk",
                "AddColonistToBoozeList", "IncreasePawnBoozeTicks", "ResetPawnBoozeTicks",
                "AddColonistAndRandomMood"
            });

            // 9. Vanilla Psycasts Expanded (VPE) & Vanilla Expanded Framework (VEF)
            PatchTypeMethodsWithUnifiedLock(harmony, "VanillaPsycastsExpanded.GenRadialCached", new string[] { "RadialDistinctThingsAround" });
            PatchTypeMethodsWithLeafLock(harmony, "VEF.Maps.VanillaExpandedFramework_Pawn_PathFollower_CostToMoveIntoCell", new string[] { "ModifySpeedFactorForPawn" });
            PatchTypeMethodsWithLeafLock(harmony, "VEF.MentalStates.VanillaExpandedFramework_Pawn_InteractionsTracker_TryInteractWith_Patch", new string[] { "TryGetCachedSpreadableComp" });
            PatchTypeMethodsWithUnifiedLock(harmony, "VEF.Pawns.PhasingPatches", new string[] { "CheckPhasing" });
            PatchTypeMethodsWithUnifiedLock(harmony, "VEF.Pawns.WorkGiver_ConstructionSkill_DeliverResourcesToBlueprints", new string[] { "HasJobOnThing", "JobOnThing" });
            PatchTypeMethodsWithUnifiedLock(harmony, "VEF.Pawns.VanillaExpandedFramework_PregnancyUtility_PregnancyChanceForPartners_Patch", new string[] { "Postfix" });
            PatchTypeMethodsWithLeafLock(harmony, "VEF.Things.VanillaExpandedFramework_ThingStuffPair_Commonality_Patch", new string[] { "GetCachedExtension" });
            PatchMethodSkipInParallel(harmony, FindModType("VEF.Weathers.VanillaExpandedFramework_WeatherManager_CurWeatherPerceived_Patch"), "Postfix", "VEF: CurWeatherPerceived_Patch.Postfix (Skip Weather Letter Check in Parallel)");

            // 10. Events+ (Leo.Events) & Prisoner Realism
            PatchTypeMethodsWithUnifiedLock(harmony, "Leo.Events.Patch_InvisibilityUtility_IsHiddenFromPlayer", new string[] { "Prefix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "Leo.Events.Patch_StunHandler_Notify_DamageApplied_EMPAdapt", new string[] { "Prefix", "Postfix" });
            PatchTypeMethodsWithUnifiedLock(harmony, "PrisonerRealism.CommonsUtility", new string[] { "PrisonerMayOpenDoor", "RoomIsCommons", "CommonsComplex" });
            PatchTypeMethodsWithUnifiedLock(harmony, "PrisonerRealism.Patch_WardenDeliverFood_Commons", new string[] { "Postfix", "CommonsHoldsEnoughFoodFor" });
            PatchTypeMethodsWithLeafLock(harmony, "PrisonerRealism.LockpickTracker", new string[] { "IsUnlocked", "MarkUnlocked", "ClearFor" });
            PatchTypeMethodsWithUnifiedLock(harmony, "PrisonerRealism.RiotUtility", new string[] { "Tick", "SmoothedRiotMtbDays", "StartRiot" });
            PatchTypeMethodsWithUnifiedLock(harmony, "PrisonerRealism.SurgeryReactionUtility", new string[] { "TryPanic", "ClearPanic" });
            PatchTypeMethodsWithUnifiedLock(harmony, "PrisonerRealism.SurrenderUtility", new string[] { "TrySurrender", "DoSurrender" });

            // 11. Popular Community Mods (Performance Optimizer, Hospitality, Dubs Bad Hygiene, Giddy-Up, Simple Sidearms, Pick Up And Haul, CommonSense)
            PatchMethodSkipInParallel(harmony, FindModType("PerformanceOptimizer.Optimization_FasterGetCompReplacement"), "Prefix", "PerformanceOptimizer: Optimization_FasterGetCompReplacement.Prefix (Skip in Parallel)");
            PatchTypeMethodsWithUnifiedLock(harmony, "Hospitality.MainTabWindow_Hospitality", new string[] { "Notify_PawnLeft" });
            PatchTypeMethodsWithUnifiedLock(harmony, "DubsBadHygiene.CompPipe", new string[] { "CompTick" });
            PatchTypeMethodsWithUnifiedLock(harmony, "PeteTimesSix.SimpleSidearms.Utilities.StatCalculator", new string[] { "canUseSidearmInstance" });
            PatchTypeMethodsWithUnifiedLock(harmony, "PickUpAndHaul.WorkGiver_HaulToInventory", new string[] { "HasJobOnThing", "JobOnThing" });
            PatchTypeMethodsWithUnifiedLock(harmony, "CommonSense.OpportunisticJobGiver", new string[] { "TryGiveJob" });

            // 12. Universal Third-Party Harmony Tick-Path & Stateful JobGiver Guard
            ScanAndProtectThirdPartyModHooks(harmony);
        }

        private static bool ZoologyAssemblyProtected = false;

        private static void ProtectZoologyModAssembly(Harmony harmony, Assembly zoologyAsm)
        {
            if (ZoologyAssemblyProtected || zoologyAsm == null || harmony == null)
            {
                return;
            }
            ZoologyAssemblyProtected = true;

            try
            {
                Type[] types = zoologyAsm.GetTypes();
                for (int i = 0; i < types.Length; i++)
                {
                    Type t = types[i];
                    if (t == null || t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition)
                    {
                        continue;
                    }

                    if (typeof(ThinkNode).IsAssignableFrom(t))
                    {
                        PatchAllOverloadsWithUnifiedLock(harmony, t, "TryGiveJob", "ZoologyMod: " + t.Name + ".TryGiveJob");
                    }
                    else if (typeof(JobDriver).IsAssignableFrom(t))
                    {
                        PatchAllOverloadsWithUnifiedLock(harmony, t, "TryMakePreToilReservations", "ZoologyMod: " + t.Name + ".TryMakePreToilReservations");
                        PatchAllOverloadsWithUnifiedLock(harmony, t, "MakeNewToils", "ZoologyMod: " + t.Name + ".MakeNewToils");
                    }
                    else if (typeof(ThingComp).IsAssignableFrom(t))
                    {
                        PatchAllOverloadsWithUnifiedLock(harmony, t, "CompTickRare", "ZoologyMod: " + t.Name + ".CompTickRare");
                        PatchAllOverloadsWithUnifiedLock(harmony, t, "PostPostApplyDamage", "ZoologyMod: " + t.Name + ".PostPostApplyDamage");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[OmniThread] ProtectZoologyModAssembly warning: " + ex.Message);
            }
        }

        private static void ScanAndProtectThirdPartyModHooks(Harmony harmony)
        {
            if (harmony == null)
            {
                return;
            }

            try
            {
                // Note: Do NOT include every-tick tracker methods (Pawn.Tick, PatherTick, HealthTick, NeedsTrackerTick, MindStateTick, InteractionsTrackerTick) here!
                // Locking every-tick methods serializes all worker threads!
                // Instead, protect low-frequency AI / job-transition / combat / interaction hooks:
                MethodBase[] coreProtectedMethods = new MethodBase[]
                {
                    AccessTools.Method(typeof(Pawn_InteractionsTracker), "TryInteractWith"),
                    AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"),
                    AccessTools.Method(typeof(Pawn_JobTracker), "StartJob"),
                    AccessTools.Method(typeof(Pawn_JobTracker), "EndCurrentJob"),
                    AccessTools.Method(typeof(Pawn_JobTracker), "TryTakeOrderedJob"),
                    AccessTools.Method(typeof(ThinkNode_JobGiver), "TryGiveJob"),
                    AccessTools.Method(typeof(Verb), "TryStartCastOn", new Type[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(bool), typeof(bool), typeof(bool), typeof(bool) }),
                    AccessTools.Method(typeof(Verb), "TryCastNextBurstShot"),
                    AccessTools.Method(typeof(FoodUtility), "BestFoodSourceOnMap"),
                    AccessTools.Method(typeof(FoodUtility), "TryFindBestFoodSourceFor"),
                    AccessTools.Method(typeof(AttackTargetFinder), "BestAttackTarget"),
                    AccessTools.Method(typeof(AttackTargetFinder), "BestShootTargetFromCurrentPosition")
                };

                for (int i = 0; i < coreProtectedMethods.Length; i++)
                {
                    MethodBase coreMethod = coreProtectedMethods[i];
                    if (coreMethod == null)
                    {
                        continue;
                    }

                    Patches patchInfo = Harmony.GetPatchInfo(coreMethod);
                    if (patchInfo == null)
                    {
                        continue;
                    }

                    ProtectHarmonyPatchList(harmony, patchInfo.Prefixes);
                    ProtectHarmonyPatchList(harmony, patchInfo.Postfixes);
                }

                if (!ThirdPartyAssembliesScanned)
                {
                    ThirdPartyAssembliesScanned = true;
                    ScanThirdPartyModSubclasses(harmony);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[OmniThread] ScanAndProtectThirdPartyModHooks warning: " + ex.Message);
            }
        }

        private static void ScanThirdPartyModSubclasses(Harmony harmony)
        {
            List<ModContentPack> runningMods = LoadedModManager.RunningModsListForReading;
            if (runningMods == null)
            {
                return;
            }

            HashSet<Assembly> visitedAssemblies = new HashSet<Assembly>();
            for (int i = 0; i < runningMods.Count; i++)
            {
                ModContentPack mod = runningMods[i];
                if (mod == null || mod.assemblies == null || mod.assemblies.loadedAssemblies == null)
                {
                    continue;
                }

                List<Assembly> asms = mod.assemblies.loadedAssemblies;
                for (int j = 0; j < asms.Count; j++)
                {
                    Assembly asm = asms[j];
                    if (asm == null || !visitedAssemblies.Add(asm))
                    {
                        continue;
                    }

                    try
                    {
                        if (asm.ReflectionOnly)
                        {
                            continue;
                        }
                        string asmName = asm.GetName().Name;
                        if (asmName == "OmniThreadUltimate" || asmName == "Assembly-CSharp" || asmName == "0Harmony" || asmName == "mscorlib" || asmName == "System")
                        {
                            continue;
                        }

                        Type[] types;
                        try
                        {
                            types = asm.GetTypes();
                        }
                        catch (ReflectionTypeLoadException rtle)
                        {
                            types = rtle.Types;
                        }
                        if (types == null)
                        {
                            continue;
                        }

                        for (int k = 0; k < types.Length; k++)
                        {
                            Type t = types[k];
                            if (t == null || t.IsAbstract || t.IsInterface || t.IsGenericTypeDefinition)
                            {
                                continue;
                            }

                            if (typeof(ThinkNode).IsAssignableFrom(t))
                            {
                                PatchAllOverloadsWithUnifiedLock(harmony, t, "TryGiveJob", "AutoThinkNode: " + t.Name + ".TryGiveJob");
                            }
                            else if (typeof(InteractionWorker).IsAssignableFrom(t))
                            {
                                PatchAllOverloadsWithUnifiedLock(harmony, t, "RandomSelectionWeight", "AutoInteraction: " + t.Name + ".RandomSelectionWeight");
                                PatchAllOverloadsWithUnifiedLock(harmony, t, "Interacted", "AutoInteraction: " + t.Name + ".Interacted");
                            }
                            else if (typeof(ThingComp).IsAssignableFrom(t) && t.FullName != "CombatAI.Comps.ThingComp_CombatAI")
                            {
                                PatchAllOverloadsWithUnifiedLock(harmony, t, "CompTickRare", "AutoCompRare: " + t.Name + ".CompTickRare");
                                PatchAllOverloadsWithUnifiedLock(harmony, t, "CompTickLong", "AutoCompLong: " + t.Name + ".CompTickLong");
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static void ProtectHarmonyPatchList(Harmony harmony, System.Collections.ObjectModel.ReadOnlyCollection<Patch> patches)
        {
            if (patches == null)
            {
                return;
            }

            MethodInfo prefixInfo = AccessTools.Method(typeof(ModCompatibilityManager), "EnterUnifiedModLock");
            MethodInfo finalizerInfo = AccessTools.Method(typeof(ModCompatibilityManager), "ExitUnifiedModLock");

            for (int i = 0; i < patches.Count; i++)
            {
                Patch p = patches[i];
                if (p == null || p.PatchMethod == null)
                {
                    continue;
                }

                MethodInfo patchMethod = p.PatchMethod;
                Type declType = patchMethod.DeclaringType;
                if (declType == null)
                {
                    continue;
                }

                Assembly asm = declType.Assembly;
                string asmName = asm.GetName().Name;
                if (asmName == "OmniThreadUltimate" || asmName == "Assembly-CSharp" || asmName == "0Harmony" || asmName == "mscorlib" || asmName == "System")
                {
                    continue;
                }

                if (PatchedMethods.Contains(patchMethod) || SkipPatchedMethods.Contains(patchMethod))
                {
                    continue;
                }

                try
                {
                    PatchedMethods.Add(patchMethod);
                    HarmonyMethod hmPrefix = new HarmonyMethod(prefixInfo);
                    hmPrefix.priority = Priority.First;
                    HarmonyMethod hmFinalizer = new HarmonyMethod(finalizerInfo);
                    hmFinalizer.priority = Priority.Last;

                    harmony.Patch(patchMethod, hmPrefix, null, null, hmFinalizer);
                    AppliedModCompatPatchesCount++;
                    AppliedCompatibilityRules.Add(string.Format("AutoGuard: {0}.{1}", declType.Name, patchMethod.Name));
                }
                catch
                {
                }
            }
        }

        public static object LeafModCacheLock
        {
            get { return ParallelTickEngine.GlobalSyncLock; }
        }

        private static void PatchTypeMethodsWithLeafLock(
            Harmony harmony,
            string fullTypeName,
            string[] methodNames)
        {
            Type targetType = FindModType(fullTypeName);
            if (targetType == null || methodNames == null)
            {
                return;
            }

            for (int i = 0; i < methodNames.Length; i++)
            {
                PatchAllOverloadsWithLeafLock(
                    harmony,
                    targetType,
                    methodNames[i],
                    targetType.Name + "." + methodNames[i] + " (Leaf)");
            }
        }

        private static void PatchAllOverloadsWithLeafLock(
            Harmony harmony,
            Type targetType,
            string methodName,
            string ruleDescription)
        {
            if (targetType == null || string.IsNullOrEmpty(methodName))
            {
                return;
            }

            try
            {
                MethodInfo prefixInfo = AccessTools.Method(typeof(ModCompatibilityManager), "EnterLeafModLock");
                MethodInfo finalizerInfo = AccessTools.Method(typeof(ModCompatibilityManager), "ExitLeafModLock");

                MethodInfo[] methods = targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                bool addedRule = false;

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo target = methods[i];
                    if (target == null || target.Name != methodName || target.IsAbstract || target.IsGenericMethodDefinition)
                    {
                        continue;
                    }
                    if (!PatchedMethods.Add(target))
                    {
                        continue;
                    }

                    HarmonyMethod hmPrefix = new HarmonyMethod(prefixInfo);
                    hmPrefix.priority = Priority.First;

                    HarmonyMethod hmFinalizer = new HarmonyMethod(finalizerInfo);
                    hmFinalizer.priority = Priority.Last;

                    harmony.Patch(target, hmPrefix, null, null, hmFinalizer);
                    AppliedModCompatPatchesCount++;
                    if (!addedRule)
                    {
                        addedRule = true;
                        AppliedCompatibilityRules.Add(ruleDescription);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(string.Format("[OmniThread] Could not patch leaf lock {0}.{1}: {2}",
                    targetType.FullName, methodName, ex.Message));
            }
        }

        private static void PatchTypeMethodsWithUnifiedLock(
            Harmony harmony,
            string fullTypeName,
            string[] methodNames)
        {
            Type targetType = FindModType(fullTypeName);
            if (targetType == null || methodNames == null)
            {
                return;
            }

            for (int i = 0; i < methodNames.Length; i++)
            {
                PatchAllOverloadsWithUnifiedLock(
                    harmony,
                    targetType,
                    methodNames[i],
                    targetType.Name + "." + methodNames[i]);
            }
        }

        private static void PatchAllOverloadsWithUnifiedLock(
            Harmony harmony,
            Type targetType,
            string methodName,
            string ruleDescription)
        {
            if (targetType == null || string.IsNullOrEmpty(methodName))
            {
                return;
            }

            try
            {
                MethodInfo prefixInfo = AccessTools.Method(typeof(ModCompatibilityManager), "EnterUnifiedModLock");
                MethodInfo finalizerInfo = AccessTools.Method(typeof(ModCompatibilityManager), "ExitUnifiedModLock");

                MethodInfo[] methods = targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                bool addedRule = false;

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo target = methods[i];
                    if (target == null || target.Name != methodName || target.IsAbstract || target.IsGenericMethodDefinition)
                    {
                        continue;
                    }
                    if (!PatchedMethods.Add(target))
                    {
                        continue;
                    }

                    HarmonyMethod hmPrefix = new HarmonyMethod(prefixInfo);
                    hmPrefix.priority = Priority.First;

                    HarmonyMethod hmFinalizer = new HarmonyMethod(finalizerInfo);
                    hmFinalizer.priority = Priority.Last;

                    harmony.Patch(target, hmPrefix, null, null, hmFinalizer);
                    AppliedModCompatPatchesCount++;
                    if (!addedRule)
                    {
                        addedRule = true;
                        AppliedCompatibilityRules.Add(ruleDescription);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(string.Format("[OmniThread] Could not patch {0}.{1}: {2}",
                    targetType.FullName, methodName, ex.Message));
            }
        }

        private static void PatchCustomPrefix(
            Harmony harmony,
            Type targetType,
            string methodName,
            string customPrefixMethodName,
            string ruleDescription)
        {
            if (targetType == null || string.IsNullOrEmpty(methodName))
            {
                return;
            }

            try
            {
                MethodInfo prefixInfo = AccessTools.Method(typeof(ModCompatibilityManager), customPrefixMethodName);
                MethodInfo[] methods = targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo target = methods[i];
                    if (target == null || target.Name != methodName || target.IsAbstract || target.IsGenericMethodDefinition)
                    {
                        continue;
                    }
                    if (!SkipPatchedMethods.Add(target))
                    {
                        continue;
                    }

                    HarmonyMethod hmPrefix = new HarmonyMethod(prefixInfo);
                    hmPrefix.priority = Priority.First;

                    harmony.Patch(target, hmPrefix, null, null, null);
                    AppliedModCompatPatchesCount++;
                    AppliedCompatibilityRules.Add(ruleDescription);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(string.Format("[OmniThread] Could not patch custom prefix {0}.{1}: {2}",
                    targetType.FullName, methodName, ex.Message));
            }
        }

        private static void PatchMethodSkipInParallel(
            Harmony harmony,
            Type targetType,
            string methodName,
            string ruleDescription)
        {
            if (targetType == null || string.IsNullOrEmpty(methodName))
            {
                return;
            }

            try
            {
                MethodInfo voidSkipPrefix = AccessTools.Method(typeof(ModCompatibilityManager), "SkipWhenInParallelPrefix");
                MethodInfo boolSkipPrefix = AccessTools.Method(typeof(ModCompatibilityManager), "SkipBoolPrefixWhenInParallel");
                MethodInfo[] methods = targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo target = methods[i];
                    if (target == null || target.Name != methodName || target.IsAbstract || target.IsGenericMethodDefinition)
                    {
                        continue;
                    }
                    if (!SkipPatchedMethods.Add(target))
                    {
                        continue;
                    }

                    MethodInfo chosenPrefix = (target.ReturnType == typeof(bool)) ? boolSkipPrefix : voidSkipPrefix;
                    HarmonyMethod hmPrefix = new HarmonyMethod(chosenPrefix);
                    hmPrefix.priority = Priority.First;

                    harmony.Patch(target, hmPrefix, null, null, null);
                    AppliedModCompatPatchesCount++;
                    AppliedCompatibilityRules.Add(ruleDescription);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(string.Format("[OmniThread] Could not patch skip prefix {0}.{1}: {2}",
                    targetType.FullName, methodName, ex.Message));
            }
        }

        public static bool SkipWhenInParallelPrefix()
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                return false;
            }
            return true;
        }

        public static bool SkipBoolPrefixWhenInParallel(ref bool __result)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                __result = true;
                return false;
            }
            return true;
        }

        // Lock-Free Cached Parallel Bypass for MissileGirl.Optimizations.StatWorker_Patch.Replacemant (replaces GetValueUnfinalized)
        public static bool BypassMissileGirlStatWorkerInParallel(
            StatWorker statWorker,
            StatRequest req,
            bool applyPostProcess,
            ref float __result)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && statWorker != null)
            {
                __result = ThreadLocalStatCache.GetOrAddUnfinalized(statWorker, req, applyPostProcess, 600);
                return false;
            }
            return true;
        }

        // Lock-Free Parallel Bypass for MissileGirl.SignatureUtility.GetSignature(Thing, bool)
        public static bool BypassMissileGirlSignatureInParallel(
            Thing thing,
            ref int __result)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                __result = thing != null ? thing.thingIDNumber : 0;
                return false;
            }
            return true;
        }

        // Lock-Free Parallel Bypass for MissileGirl.Optimizations.GenTemperature_Patch.Prefix
        public static bool BypassMissileGirlGenTempPrefixInParallel(
            ref bool __state,
            ref bool __result)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                __state = false;
                __result = true; // Tells Harmony to run original GenTemperature.ComfortableTemperatureRange(Pawn) without touching tempCache
                return false;
            }
            return true;
        }

        // Lock-Free Parallel Bypass for CombatAI.StatCache.GetStatValue_Fast(Thing, StatDef, int, bool)
        public static bool BypassCombatAIStatCacheInParallel(
            Thing thing,
            StatDef stat,
            bool applyPostProcess,
            ref float __result)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                if (thing == null)
                {
                    __result = stat != null ? stat.defaultBaseValue : 0f;
                }
                else if (stat != null && stat.Worker != null)
                {
                    __result = ThreadLocalStatCache.GetOrAdd(stat.Worker, thing, applyPostProcess, 600);
                }
                else
                {
                    __result = StatExtension.GetStatValue(thing, stat, applyPostProcess, 600);
                }
                return false;
            }
            return true;
        }

        // Null-Keep Cache Entry on CombatAI.CompCache.Notify_ThingDestroyed(ThingWithComps)
        public static bool CombatAINotifyThingDestroyedPrefix(ThingWithComps thing)
        {
            if (thing != null && CAICompDicts != null)
            {
                int id = thing.thingIDNumber;
                if (MainThreadSyncQueue.InParallelSection)
                {
                    MainThreadSyncQueue.Enqueue(delegate
                    {
                        if (CAICompDicts != null)
                        {
                            object bId = id;
                            for (int i = 0; i < CAICompDicts.Length; i++)
                            {
                                CAICompDicts[i][bId] = null;
                            }
                        }
                    });
                }
                else
                {
                    object boxedId = id;
                    for (int i = 0; i < CAICompDicts.Length; i++)
                    {
                        CAICompDicts[i][boxedId] = null;
                    }
                }
            }
            return false;
        }

        public static void EnterLeafModLock(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(LeafModCacheLock, ref __state);
            }
        }

        public static Exception ExitLeafModLock(Exception __exception, bool __state)
        {
            if (__state)
            {
                Monitor.Exit(LeafModCacheLock);
            }
            return __exception;
        }

        public static void EnterUnifiedModLock(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
        }

        public static Exception ExitUnifiedModLock(Exception __exception, bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
            return __exception;
        }
    }
}
