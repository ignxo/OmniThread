using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace OmniThreadUltimate
{
    public static class AutoBenchmarkRunner
    {
        public static int TargetBenchmarkTicks = 3000;
        public const int TargetSpawnPawnCount = 250;
        public const int WarmupTicksAfterMapLoad = 120;

        public static bool IsAutoTestMode = false;
        public static string AutoTestSaveName = null;
        public static bool MapLaunchTriggered = false;
        public static bool PawnsSpawned = false;
        public static bool IsMeasuring = false;
        public static bool BenchmarkFinished = false;
        public static bool ShutdownRequested = false;

        public static int MapLoadedTickCount = 0;
        public static int RecordedTicks = 0;
        public static int SpawnedColonistsCount = 0;
        public static int SpawnedRaidersCount = 0;
        public static int ShutdownDelayFrames = 0;

        private static readonly List<float> TickDurationsMs = new List<float>(TargetBenchmarkTicks + 64);
        private static readonly List<float> TickListDurationsMs = new List<float>(TargetBenchmarkTicks + 64);
        private static readonly List<int> TickActiveThreadCounts = new List<int>(TargetBenchmarkTicks + 64);
        private static readonly Stopwatch WallClockStopwatch = new Stopwatch();
        private static readonly Stopwatch HeartbeatStopwatch = new Stopwatch();

        public static void InitializeFromCommandLine()
        {
            IsAutoTestMode = GenCommandLine.CommandLineArgPassed("autotest");
            string saveVal;
            if (GenCommandLine.TryGetCommandLineArg("autotest-save", out saveVal) && !string.IsNullOrEmpty(saveVal))
            {
                AutoTestSaveName = saveVal.Trim();
                IsAutoTestMode = true;
            }
            string ticksVal;
            if (GenCommandLine.TryGetCommandLineArg("autotest-ticks", out ticksVal) && !string.IsNullOrEmpty(ticksVal))
            {
                int parsedTicks;
                if (int.TryParse(ticksVal.Trim(), out parsedTicks) && parsedTicks > 10)
                {
                    TargetBenchmarkTicks = parsedTicks;
                }
            }
            string iterVal;
            if (GenCommandLine.TryGetCommandLineArg("omnithread-iter", out iterVal) && !string.IsNullOrEmpty(iterVal))
            {
                int parsedIter;
                if (int.TryParse(iterVal.Trim(), out parsedIter))
                {
                    OmniThreadMod.IterationLevel = parsedIter;
                    OmniThreadMod.EnableMultithreading = (parsedIter > 0);
                }
            }

            if (IsAutoTestMode)
            {
                OmniThreadGUI.ShowOverlay = true;
                HeartbeatStopwatch.Restart();
                Log.Message(string.Format("[OmniThread Ultimate] -autotest detected! Save='{0}', TargetTicks={1}, IterationLevel={2}, EnableMultithreading={3}",
                    AutoTestSaveName ?? "<quicktest>", TargetBenchmarkTicks, OmniThreadMod.IterationLevel, OmniThreadMod.EnableMultithreading));
                EnsureModActiveInModsConfig();
            }
        }

        public static void EnsureModActiveInModsConfig()
        {
            try
            {
                if (!ModsConfig.IsActive("omnithread.ultimate"))
                {
                    ModsConfig.SetActive("omnithread.ultimate", true);
                    ModsConfig.Save();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[OmniThread Ultimate] EnsureModActiveInModsConfig warning: " + ex.Message);
            }
        }

        public static void TryTriggerQuickTestMap()
        {
            if (!IsAutoTestMode || MapLaunchTriggered)
            {
                return;
            }
            if (Current.ProgramState != ProgramState.Entry)
            {
                return;
            }

            MapLaunchTriggered = true;
            if (!string.IsNullOrEmpty(AutoTestSaveName))
            {
                Log.Message("[OmniThread Ultimate] AutoTest: Triggering automatic load of save '" + AutoTestSaveName + "'...");
                GameDataSaveLoader.LoadGame(AutoTestSaveName);
                return;
            }

            Log.Message("[OmniThread Ultimate] AutoTest: Triggering automatic test map generation and load...");

            LongEventHandler.QueueLongEvent(delegate
            {
                Rand.Seed = 123456789;
                Root_Play.SetupForQuickTestPlay();
                if (Current.Game != null && Current.Game.InitData != null)
                {
                    Current.Game.InitData.mapSize = 250;
                    Current.Game.InitData.permadeath = false;
                }
                PageUtility.InitGameStart();
            }, "GeneratingMap", false, new Action<Exception>(GameAndMapInitExceptionHandlers.ErrorWhileGeneratingMap), true, false, null);
        }

        public static void OnRootPlayUpdate()
        {
            if (!IsAutoTestMode)
            {
                return;
            }

            if (ShutdownRequested)
            {
                ShutdownDelayFrames++;
                if (ShutdownDelayFrames >= 8)
                {
                    if (Current.Game != null && Current.Game.Info != null)
                    {
                        Current.Game.Info.permadeathMode = false;
                    }
                    Log.Message("[OmniThread Ultimate] AutoTest: Calling Environment.Exit(0)...");
                    Environment.Exit(0);
                }
                return;
            }

            if (Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null || Find.TickManager == null)
            {
                return;
            }

            if (HeartbeatStopwatch.ElapsedMilliseconds >= 4000)
            {
                HeartbeatStopwatch.Restart();
                Log.Message(string.Format(CultureInfo.InvariantCulture,
                    "[OmniThread Heartbeat] Frame={0} | RecordedTicks={1}/{2} | LastTick={3:F2} ms | RollingAvg={4:F2} ms | RoutedModTicks={5} | CaughtEx={6}",
                    Time.frameCount,
                    RecordedTicks,
                    TargetBenchmarkTicks,
                    ParallelTickEngine.LastTickMs,
                    ParallelTickEngine.RollingAvgTickMs,
                    ModCompatibilityManager.MainThreadRoutedModThingTicks,
                    MainThreadSyncQueue.CaughtConcurrencyExceptions));
            }

            if (Find.TickManager.CurTimeSpeed != TimeSpeed.Ultrafast)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }

            if (!PawnsSpawned)
            {
                MapLoadedTickCount++;
                if (MapLoadedTickCount >= WarmupTicksAfterMapLoad)
                {
                    if (string.IsNullOrEmpty(AutoTestSaveName))
                    {
                        SpawnBenchmarkPawns(Find.CurrentMap);
                    }
                    else
                    {
                        if (Current.Game != null && Current.Game.Info != null)
                        {
                            Current.Game.Info.permadeathMode = false;
                        }
                        Log.Message(string.Format("[OmniThread Ultimate] AutoTest Save '{0}' active! Existing spawned pawns on map = {1}. Starting {2}-tick benchmark at TimeSpeed.Ultrafast...",
                            AutoTestSaveName, Find.CurrentMap.mapPawns.AllPawnsSpawnedCount, TargetBenchmarkTicks));
                    }
                    PawnsSpawned = true;
                    IsMeasuring = true;
                    RecordedTicks = 0;
                    TickDurationsMs.Clear();
                    TickListDurationsMs.Clear();
                    TickActiveThreadCounts.Clear();
                    ParallelTickEngine.ResetThreadWorkCounts();
                    ParallelTickEngine.TotalParallelThingTicks = 0;
                    ParallelTickEngine.TotalNeedsParallelTicks = 0;
                    ParallelTickEngine.TotalTargetsParallelScans = 0;
                    ParallelTickEngine.TotalMapPostParallelSubsystems = 0;
                    MainThreadSyncQueue.TotalEnqueuedActions = 0;
                    MainThreadSyncQueue.TotalFlushedActions = 0;
                    MainThreadSyncQueue.CaughtConcurrencyExceptions = 0;
                    MainThreadSyncQueue.PreventedThreadHazards = 0;
                    ModCompatibilityManager.MainThreadRoutedModThingTicks = 0;
                    WallClockStopwatch.Restart();
                    if (string.IsNullOrEmpty(AutoTestSaveName))
                    {
                        Log.Message("[OmniThread Ultimate] AutoTest: 250 Pawns spawned! Starting 3000-tick benchmark at TimeSpeed.Ultrafast...");
                    }
                }
            }
        }

        private static void SpawnBenchmarkPawns(Map map)
        {
            Log.Message("[OmniThread Ultimate] AutoTest: Spawning 250 colonists and raiders across map quadrants...");
            Faction playerFaction = Faction.OfPlayer;
            Faction hostileFaction = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Pirate);
            if (hostileFaction == null)
            {
                foreach (Faction f in Find.FactionManager.AllFactionsListForReading)
                {
                    if (f != null && !f.IsPlayer && f.HostileTo(playerFaction) && f.def.humanlikeFaction)
                    {
                        hostileFaction = f;
                        break;
                    }
                }
            }
            if (hostileFaction == null)
            {
                foreach (Faction f in Find.FactionManager.AllFactionsListForReading)
                {
                    if (f != null && !f.IsPlayer && f.HostileTo(playerFaction))
                    {
                        hostileFaction = f;
                        break;
                    }
                }
            }

            int colonistsToSpawn = 125;
            int raidersToSpawn = 125;

            IntVec3 center = map.Center;
            int offset = 28;
            IntVec3[] quadrantCenters = new IntVec3[]
            {
                new IntVec3(center.x - offset, 0, center.z - offset),
                new IntVec3(center.x + offset, 0, center.z - offset),
                new IntVec3(center.x - offset, 0, center.z + offset),
                new IntVec3(center.x + offset, 0, center.z + offset)
            };

            PawnKindDef colonistKind = PawnKindDefOf.Colonist;
            PawnKindDef raiderKind = PawnKindDefOf.Drifter;
            if (DefDatabase<PawnKindDef>.GetNamedSilentFail("Pirate") != null)
            {
                raiderKind = DefDatabase<PawnKindDef>.GetNamedSilentFail("Pirate");
            }

            for (int i = 0; i < colonistsToSpawn; i++)
            {
                try
                {
                    IntVec3 anchor = quadrantCenters[i % 4];
                    IntVec3 spawnLoc = FindWalkableCellNear(map, anchor, 24, i);
                    PawnGenerationRequest req = new PawnGenerationRequest(
                        colonistKind,
                        playerFaction,
                        PawnGenerationContext.NonPlayer,
                        -1,
                        true,
                        false,
                        false,
                        false,
                        false,
                        0f,
                        false,
                        true,
                        false,
                        false,
                        false);
                    Pawn p = PawnGenerator.GeneratePawn(req);
                    if (p != null)
                    {
                        if (p.equipment != null)
                        {
                            p.equipment.DestroyAllEquipment();
                        }
                        GenSpawn.Spawn(p, spawnLoc, map, Rot4.Random, WipeMode.Vanish, false);
                        SpawnedColonistsCount++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[OmniThread Ultimate] Error spawning colonist: " + ex.Message);
                }
            }

            List<Pawn> spawnedRaiders = new List<Pawn>(raidersToSpawn);
            for (int j = 0; j < raidersToSpawn; j++)
            {
                try
                {
                    IntVec3 anchor = quadrantCenters[(j + 2) % 4];
                    IntVec3 spawnLoc = FindWalkableCellNear(map, anchor, 28, j + 500);
                    PawnGenerationRequest req = new PawnGenerationRequest(
                        raiderKind,
                        hostileFaction,
                        PawnGenerationContext.NonPlayer,
                        -1,
                        true,
                        false,
                        false,
                        false,
                        false,
                        0f,
                        false,
                        true,
                        false,
                        false,
                        false);
                    Pawn p = PawnGenerator.GeneratePawn(req);
                    if (p != null)
                    {
                        if (p.equipment != null)
                        {
                            p.equipment.DestroyAllEquipment();
                        }
                        GenSpawn.Spawn(p, spawnLoc, map, Rot4.Random, WipeMode.Vanish, false);
                        spawnedRaiders.Add(p);
                        SpawnedRaidersCount++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[OmniThread Ultimate] Error spawning raider: " + ex.Message);
                }
            }

            if (hostileFaction != null && spawnedRaiders.Count > 0)
            {
                try
                {
                    LordMaker.MakeNewLord(hostileFaction, new LordJob_AssaultColony(hostileFaction, false, false, false, false, true, false, false), map, spawnedRaiders);
                }
                catch (Exception ex)
                {
                    Log.Warning("[OmniThread Ultimate] Error creating assault lord: " + ex.Message);
                }
            }

            Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            Log.Message(string.Format("[OmniThread Ultimate] AutoTest: Total spawned pawns on map = {0} (Colonists spawned: {1}, Raiders spawned: {2})",
                map.mapPawns.AllPawnsSpawnedCount, SpawnedColonistsCount, SpawnedRaidersCount));
        }

        private static IntVec3 FindWalkableCellNear(Map map, IntVec3 anchor, int radius, int seedOffset)
        {
            int numRings = Math.Min(GenRadial.RadialPattern.Length, 900);
            int startIdx = (seedOffset * 17) % numRings;
            for (int i = 0; i < numRings; i++)
            {
                int idx = (startIdx + i) % numRings;
                IntVec3 c = anchor + GenRadial.RadialPattern[idx];
                if (c.InBounds(map) && c.Standable(map) && !c.Fogged(map))
                {
                    return c;
                }
            }
            IntVec3 result;
            if (CellFinder.TryFindRandomCellNear(anchor, map, radius, delegate(IntVec3 c) { return c.InBounds(map) && c.Standable(map); }, out result))
            {
                return result;
            }
            return map.Center;
        }

        public static void NotifySingleTickCompleted(float tickMs, int activeWorkers)
        {
            if (!IsAutoTestMode || !IsMeasuring || BenchmarkFinished)
            {
                return;
            }

            if (Find.TickManager != null && Find.TickManager.CurTimeSpeed != TimeSpeed.Ultrafast)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
            }

            RecordedTicks++;
            TickDurationsMs.Add(tickMs);
            TickListDurationsMs.Add(ParallelTickEngine.LastTickListMs);
            TickActiveThreadCounts.Add(activeWorkers);

            if (RecordedTicks % 200 == 0 || RecordedTicks == 1)
            {
                double elapsedSec = WallClockStopwatch.Elapsed.TotalSeconds;
                double currentWallTps = elapsedSec > 0.001 ? (RecordedTicks / elapsedSec) : 0.0;
                Log.Message(string.Format(CultureInfo.InvariantCulture,
                    "[OmniThread AutoTest] Progress: {0}/{1} ticks | LastTick={2:F3} ms (TickList={3:F3} ms) | RollingAvg={4:F3} ms | WallTPS={5:F1} | Workers={6} | SyncFlushed={7} | CaughtExceptions={8}",
                    RecordedTicks,
                    TargetBenchmarkTicks,
                    tickMs,
                    ParallelTickEngine.LastTickListMs,
                    ParallelTickEngine.RollingAvgTickMs,
                    currentWallTps,
                    activeWorkers,
                    MainThreadSyncQueue.TotalFlushedActions,
                    MainThreadSyncQueue.CaughtConcurrencyExceptions));
            }

            if (RecordedTicks >= TargetBenchmarkTicks)
            {
                WallClockStopwatch.Stop();
                IsMeasuring = false;
                BenchmarkFinished = true;
                CompleteBenchmarkAndShutdown();
            }
        }

        private static void CompleteBenchmarkAndShutdown()
        {
            try
            {
                WriteBenchmarkResultsJson();
            }
            catch (Exception ex)
            {
                Log.Error("[OmniThread Ultimate] Failed to write benchmark_results.json: " + ex);
            }

            try
            {
                string screenshotPath = "d:/rimworld_multithread_workspace/autotest_screenshot.png";
                string iterScreenshotPath = string.Format("d:/rimworld_multithread_workspace/screenshot_iter_{0}.png", OmniThreadMod.IterationLevel);
                ScreenCapture.CaptureScreenshot(screenshotPath);
                ScreenCapture.CaptureScreenshot(iterScreenshotPath);
                Log.Message("[OmniThread Ultimate] Captured screenshot to " + screenshotPath);
            }
            catch (Exception ex)
            {
                Log.Warning("[OmniThread Ultimate] Screenshot warning: " + ex.Message);
            }

            ShutdownRequested = true;
            ShutdownDelayFrames = 0;
        }

        private static void WriteBenchmarkResultsJson()
        {
            int n = TickDurationsMs.Count;
            if (n == 0)
            {
                return;
            }

            ParallelTickEngine.SyncThreadWorkDictionary();

            List<float> sorted = new List<float>(TickDurationsMs);
            sorted.Sort();

            double sumMs = 0.0;
            for (int i = 0; i < n; i++)
            {
                sumMs += TickDurationsMs[i];
            }
            double avgMs = sumMs / n;
            double minMs = sorted[0];
            double maxMs = sorted[n - 1];
            double p50Ms = sorted[n / 2];
            double p95Ms = sorted[(int)(n * 0.95)];
            double p99Ms = sorted[(int)(n * 0.99)];

            double sumTickListMs = 0.0;
            int tlCount = TickListDurationsMs.Count;
            for (int i = 0; i < tlCount; i++)
            {
                sumTickListMs += TickListDurationsMs[i];
            }
            double avgTickListMs = tlCount > 0 ? (sumTickListMs / tlCount) : 0.0;
            double tickListComputeTps = avgTickListMs > 0.0001 ? (1000.0 / avgTickListMs) : 0.0;

            double computeTps = avgMs > 0.0001 ? (1000.0 / avgMs) : 0.0;
            double wallClockSeconds = WallClockStopwatch.Elapsed.TotalSeconds;
            double wallClockTps = wallClockSeconds > 0.001 ? (n / wallClockSeconds) : 0.0;

            double sumWorkers = 0.0;
            int maxWorkers = 1;
            for (int i = 0; i < TickActiveThreadCounts.Count; i++)
            {
                int w = TickActiveThreadCounts[i];
                sumWorkers += w;
                if (w > maxWorkers)
                {
                    maxWorkers = w;
                }
            }
            double avgActiveWorkers = TickActiveThreadCounts.Count > 0 ? (sumWorkers / TickActiveThreadCounts.Count) : 1.0;
            int distinctThreads = Math.Max(1, ParallelTickEngine.ThreadWorkCounts.Count);

            int spawnedPawnsNow = Find.CurrentMap != null ? Find.CurrentMap.mapPawns.AllPawnsSpawnedCount : TargetSpawnPawnCount;

            StringBuilder sb = new StringBuilder(4096);
            sb.AppendLine("{");
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"timestamp\": \"{0}\",\n", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"modName\": \"OmniThread Ultimate\",\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"saveName\": \"{0}\",\n", AutoTestSaveName ?? "quicktest");
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"iterationLevel\": {0},\n", OmniThreadMod.IterationLevel);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"multithreadingEnabled\": {0},\n", OmniThreadMod.EnableMultithreading ? "true" : "false");
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"logicalProcessors\": {0},\n", Environment.ProcessorCount);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"totalTicksMeasured\": {0},\n", n);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"spawnedPawnsOnMap\": {0},\n", spawnedPawnsNow);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"spawnedColonists\": {0},\n", SpawnedColonistsCount);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"spawnedRaiders\": {0},\n", SpawnedRaidersCount);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"avgMsPerTick\": {0:F4},\n", avgMs);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"avgTickListMs\": {0:F4},\n", avgTickListMs);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"minMsPerTick\": {0:F4},\n", minMs);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"p50MsPerTick\": {0:F4},\n", p50Ms);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"p95MsPerTick\": {0:F4},\n", p95Ms);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"p99MsPerTick\": {0:F4},\n", p99Ms);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"maxMsPerTick\": {0:F4},\n", maxMs);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"computeAverageTPS\": {0:F2},\n", computeTps);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"tickListComputeTPS\": {0:F2},\n", tickListComputeTps);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"wallClockAverageTPS\": {0:F2},\n", wallClockTps);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"wallClockDurationSeconds\": {0:F3},\n", wallClockSeconds);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"distinctWorkerThreadsUsed\": {0},\n", distinctThreads);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"avgActiveWorkerThreadsPerTick\": {0:F2},\n", avgActiveWorkers);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"maxActiveWorkerThreadsPerTick\": {0},\n", maxWorkers);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"totalParallelThingTicks\": {0},\n", ParallelTickEngine.TotalParallelThingTicks);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"totalNeedsParallelTicks\": {0},\n", ParallelTickEngine.TotalNeedsParallelTicks);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"totalTargetsParallelScans\": {0},\n", ParallelTickEngine.TotalTargetsParallelScans);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"totalMapPostParallelSubsystems\": {0},\n", ParallelTickEngine.TotalMapPostParallelSubsystems);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"mainThreadSyncEnqueued\": {0},\n", MainThreadSyncQueue.TotalEnqueuedActions);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"mainThreadSyncFlushed\": {0},\n", MainThreadSyncQueue.TotalFlushedActions);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"preventedThreadHazards\": {0},\n", MainThreadSyncQueue.PreventedThreadHazards);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"caughtConcurrencyExceptions\": {0},\n", MainThreadSyncQueue.CaughtConcurrencyExceptions);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"activeModsCount\": {0},\n", ModCompatibilityManager.DetectedActiveModsCount);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"appliedModCompatPatches\": {0},\n", ModCompatibilityManager.AppliedModCompatPatchesCount);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"autoSafeModFallbacks\": {0},\n", ModCompatibilityManager.AutoSafeModFallbackTypesCount);
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"mainThreadRoutedModThingTicks\": {0},\n", ModCompatibilityManager.MainThreadRoutedModThingTicks);
            sb.AppendLine("  \"workerThreadBreakdown\": {");

            int tIdx = 0;
            int tTotal = ParallelTickEngine.ThreadWorkCounts.Count;
            foreach (KeyValuePair<int, long> kvp in ParallelTickEngine.ThreadWorkCounts)
            {
                tIdx++;
                string comma = (tIdx < tTotal) ? "," : "";
                sb.AppendFormat(CultureInfo.InvariantCulture, "    \"thread_{0}\": {1}{2}\n", kvp.Key, kvp.Value, comma);
            }
            sb.AppendLine("  }");
            sb.AppendLine("}");

            string json = sb.ToString();
            string mainPath = "d:/rimworld_multithread_workspace/benchmark_results.json";
            string iterPath = string.Format("d:/rimworld_multithread_workspace/benchmark_iter_{0}.json", OmniThreadMod.IterationLevel);

            File.WriteAllText(mainPath, json, Encoding.UTF8);
            File.WriteAllText(iterPath, json, Encoding.UTF8);

            string recEx;
            int exPrinted = 0;
            while (exPrinted < 15 && MainThreadSyncQueue.RecentExceptions.TryDequeue(out recEx))
            {
                exPrinted++;
                Log.Message("[OmniThread Diagnostic] " + recEx);
            }

            Log.Message("[OmniThread Ultimate] Benchmark complete! Results written to " + mainPath + " and " + iterPath);
        }
    }

    [HarmonyPatch(typeof(MainMenuDrawer), "Init")]
    public static class Patch_MainMenuDrawer_Init
    {
        public static void Postfix()
        {
            AutoBenchmarkRunner.TryTriggerQuickTestMap();
        }
    }

    [HarmonyPatch(typeof(MainMenuDrawer), "MainMenuOnGUI")]
    public static class Patch_MainMenuDrawer_MainMenuOnGUI
    {
        public static void Postfix()
        {
            AutoBenchmarkRunner.TryTriggerQuickTestMap();
        }
    }

    [HarmonyPatch(typeof(Root_Play), "Update")]
    public static class Patch_Root_Play_Update
    {
        public static void Postfix()
        {
            AutoBenchmarkRunner.OnRootPlayUpdate();
        }
    }

    [HarmonyPatch(typeof(TickManager), "get_TickRateMultiplier")]
    public static class Patch_TickManager_TickRateMultiplier
    {
        [ThreadStatic]
        private static bool tsInGetter;

        [HarmonyPriority(Priority.First)]
        public static bool Prefix(TickManager __instance, ref float __result)
        {
            if (tsInGetter || __instance == null)
            {
                return true;
            }
            try
            {
                tsInGetter = true;
                if (AutoBenchmarkRunner.IsAutoTestMode && __instance.CurTimeSpeed == TimeSpeed.Ultrafast)
                {
                    __result = 150f;
                    return false;
                }

                if (OmniThreadMod.UncapMaxTimeSpeed)
                {
                    TimeSpeed speed = __instance.CurTimeSpeed;
                    if (speed == TimeSpeed.Superfast || speed == TimeSpeed.Ultrafast)
                    {
                        __result = 150f;
                        return false;
                    }
                }
            }
            finally
            {
                tsInGetter = false;
            }

            return true;
        }

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(TickManager __instance, ref float __result)
        {
            if (tsInGetter || __instance == null)
            {
                return;
            }
            try
            {
                tsInGetter = true;
                if (OmniThreadMod.UncapMaxTimeSpeed)
                {
                    TimeSpeed speed = __instance.CurTimeSpeed;
                    if (speed == TimeSpeed.Superfast || speed == TimeSpeed.Ultrafast)
                    {
                        __result = 150f;
                    }
                }
            }
            finally
            {
                tsInGetter = false;
            }
        }
    }

    [HarmonyPatch(typeof(TickManager), "get_ForcePaused")]
    public static class Patch_TickManager_ForcePaused
    {
        public static bool Prefix(ref bool __result)
        {
            if (AutoBenchmarkRunner.IsAutoTestMode && !AutoBenchmarkRunner.BenchmarkFinished)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
}
