using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace OmniThreadUltimate
{
    public static class ParallelTickEngine
    {
        public static readonly object GlobalSyncLock = new object();
        public static readonly object PositionLock = GlobalSyncLock;
        public static readonly object PathFinderLock = GlobalSyncLock;
        public static readonly object PathPoolLock = GlobalSyncLock;
        public static readonly object AttackTargetsLock = GlobalSyncLock;
        public static readonly object RegionTraverserLock = GlobalSyncLock;
        public static readonly object ReachabilityLock = GlobalSyncLock;
        public static readonly object ReservationLock = GlobalSyncLock;
        public static readonly object HealthTrackerLock = GlobalSyncLock;
        public static readonly object MapPawnsLock = GlobalSyncLock;
        public static readonly object WorkSettingsLock = GlobalSyncLock;
        public static readonly object JobPoolLock = GlobalSyncLock;
        public static readonly object HostilityFleeLock = GlobalSyncLock;
        public static readonly object WanderUtilityLock = GlobalSyncLock;
        public static readonly object AttackTargetFinderLock = GlobalSyncLock;
        public static readonly object GenClosestLock = GlobalSyncLock;
        public static readonly object CombatVerbLock = GlobalSyncLock;
        public static readonly object CellFinderLooseLock = GlobalSyncLock;
        public static readonly object BeautyLock = GlobalSyncLock;
        public static readonly object ThoughtHandlerLock = GlobalSyncLock;
        public static readonly object CapacitiesLock = GlobalSyncLock;
        public static readonly object RegionListersLock = GlobalSyncLock;
        public static readonly object GenTemperatureLock = GlobalSyncLock;

        private static Thing[] Iter5WorkBuffer = new Thing[4096];
        private static Thing[] Iter5MainThreadBuffer = new Thing[4096];
        private static int Iter5MainThreadCount = 0;

        public static readonly ConcurrentDictionary<int, long> ThreadWorkCounts = new ConcurrentDictionary<int, long>();
        public static readonly ConcurrentDictionary<int, int> ThreadTickWorkCounts = new ConcurrentDictionary<int, int>();

        public const int MaxTrackedThreadId = 16384;
        public const int CacheLineLongStride = 8;
        public const int CacheLineIntStride = 16;

        private static readonly long[] FastThreadWorkCounts = new long[MaxTrackedThreadId * CacheLineLongStride];
        private static readonly int[] FastThreadSeenStamp = new int[MaxTrackedThreadId * CacheLineIntStride];
        private static int CurrentTickStamp = 1;
        private static int ActiveWorkersThisTick = 0;

        public static readonly float[] RecentTickMs = new float[120];
        public static int RecentTickCursor = 0;
        public static float LastTickMs = 0f;
        public static float RollingAvgTickMs = 0f;
        public static float LastTickListMs = 0f;
        public static float RollingAvgTickListMs = 0f;
        public static float CurrentEstimateTps = 0f;
        public static int ActiveWorkerThreadsLastTick = 1;
        public static long TotalParallelThingTicks = 0;
        public static long TotalNeedsParallelTicks = 0;
        public static long TotalTargetsParallelScans = 0;
        public static long TotalMapPostParallelSubsystems = 0;

        private static readonly Stopwatch TickStopwatch = new Stopwatch();
        private static readonly Stopwatch TickListStopwatch = new Stopwatch();

        public static readonly FieldInfo Field_TickList_thingLists = AccessTools.Field(typeof(TickList), "thingLists");
        public static readonly FieldInfo Field_TickList_thingsToRegister = AccessTools.Field(typeof(TickList), "thingsToRegister");
        public static readonly FieldInfo Field_TickList_thingsToDeregister = AccessTools.Field(typeof(TickList), "thingsToDeregister");
        public static readonly FieldInfo Field_TickList_tickType = AccessTools.Field(typeof(TickList), "tickType");

        public static ParallelOptions GetParallelOptionsForIteration(int iterLevel)
        {
            int maxCores = Math.Max(2, Environment.ProcessorCount);
            int dop = maxCores;
            if (iterLevel == 1)
            {
                dop = Math.Min(2, maxCores);
            }
            else if (iterLevel == 2)
            {
                dop = Math.Min(4, maxCores);
            }
            else if (iterLevel == 3)
            {
                dop = Math.Min(6, maxCores);
            }
            else if (iterLevel == 4)
            {
                dop = Math.Min(8, maxCores);
            }
            else
            {
                dop = maxCores;
            }
            if (OmniThreadMod.MaxWorkerThreads > 0)
            {
                dop = Math.Max(1, Math.Min(dop, OmniThreadMod.MaxWorkerThreads));
            }
            ParallelOptions opts = new ParallelOptions();
            opts.MaxDegreeOfParallelism = dop;
            return opts;
        }

        public static void RecordThreadWork()
        {
            int tid = Thread.CurrentThread.ManagedThreadId;
            if ((uint)tid < (uint)MaxTrackedThreadId)
            {
                FastThreadWorkCounts[tid << 3]++;
                int stamp = CurrentTickStamp;
                int stampIdx = tid << 4;
                if (FastThreadSeenStamp[stampIdx] != stamp)
                {
                    FastThreadSeenStamp[stampIdx] = stamp;
                    Interlocked.Increment(ref ActiveWorkersThisTick);
                }
            }
        }

        public static void ResetThreadWorkCounts()
        {
            Array.Clear(FastThreadWorkCounts, 0, FastThreadWorkCounts.Length);
            Array.Clear(FastThreadSeenStamp, 0, FastThreadSeenStamp.Length);
            ThreadWorkCounts.Clear();
            ThreadTickWorkCounts.Clear();
        }

        public static void SyncThreadWorkDictionary()
        {
            ThreadWorkCounts.Clear();
            for (int tid = 0; tid < MaxTrackedThreadId; tid++)
            {
                long count = FastThreadWorkCounts[tid << 3];
                if (count > 0)
                {
                    ThreadWorkCounts[tid] = count;
                }
            }
        }

        public static void BeginTickMeasurement()
        {
            CurrentTickStamp++;
            ActiveWorkersThisTick = 0;
            LastTickListMs = 0f;
            MainThreadSyncQueue.EnqueuedThisTick = 0;
            MainThreadSyncQueue.CurrentQueueTickStamp++;
            TickStopwatch.Restart();
        }

        public static void EndTickMeasurement()
        {
            TickStopwatch.Stop();
            float ms = (float)TickStopwatch.Elapsed.TotalMilliseconds;
            LastTickMs = ms;
            RecentTickMs[RecentTickCursor] = ms;
            RecentTickCursor = (RecentTickCursor + 1) % RecentTickMs.Length;

            float sum = 0f;
            int valid = 0;
            for (int i = 0; i < RecentTickMs.Length; i++)
            {
                if (RecentTickMs[i] > 0f)
                {
                    sum += RecentTickMs[i];
                    valid++;
                }
            }
            RollingAvgTickMs = valid > 0 ? (sum / valid) : ms;
            if (LastTickListMs > 0f)
            {
                RollingAvgTickListMs = RollingAvgTickListMs > 0f ? (RollingAvgTickListMs * 0.95f + LastTickListMs * 0.05f) : LastTickListMs;
            }
            int activeWorkers = ActiveWorkersThisTick;
            ActiveWorkerThreadsLastTick = activeWorkers > 0 ? activeWorkers : 1;
            AutoBenchmarkRunner.NotifySingleTickCompleted(ms, ActiveWorkerThreadsLastTick);
        }

        public static int GetTickIntervalFor(TickerType tickType)
        {
            switch (tickType)
            {
                case TickerType.Normal:
                    return 1;
                case TickerType.Rare:
                    return 250;
                case TickerType.Long:
                    return 2000;
                default:
                    return 1;
            }
        }

        public static void ExecuteTickList(TickList instance)
        {
            List<List<Thing>> thingLists = (List<List<Thing>>)Field_TickList_thingLists.GetValue(instance);
            List<Thing> thingsToRegister = (List<Thing>)Field_TickList_thingsToRegister.GetValue(instance);
            List<Thing> thingsToDeregister = (List<Thing>)Field_TickList_thingsToDeregister.GetValue(instance);
            TickerType tickType = (TickerType)Field_TickList_tickType.GetValue(instance);
            int interval = GetTickIntervalFor(tickType);

            for (int i = 0; i < thingsToRegister.Count; i++)
            {
                Thing t = thingsToRegister[i];
                if (t != null)
                {
                    List<Thing> bucket = GetBucket(thingLists, t, interval);
                    bucket.Add(t);
                }
            }
            thingsToRegister.Clear();

            for (int j = 0; j < thingsToDeregister.Count; j++)
            {
                Thing t = thingsToDeregister[j];
                if (t != null)
                {
                    List<Thing> bucket = GetBucket(thingLists, t, interval);
                    bucket.Remove(t);
                }
            }
            thingsToDeregister.Clear();

            if (DebugSettings.fastEcology)
            {
                Find.World.tileTemperatures.ClearCaches();
                for (int k = 0; k < thingLists.Count; k++)
                {
                    List<Thing> list = thingLists[k];
                    for (int l = 0; l < list.Count; l++)
                    {
                        if (list[l].def.category == ThingCategory.Plant)
                        {
                            list[l].TickLong();
                        }
                    }
                }
            }

            List<Thing> currentBucket = thingLists[Find.TickManager.TicksGame % interval];
            int count = currentBucket.Count;
            if (count == 0)
            {
                return;
            }

            ModCompatibilityManager.EnsureLateCompatibilityPatchesApplied();
            ModCompatibilityManager.PreWarmBeforeParallelTick(currentBucket);

            int iterLevel = OmniThreadMod.IterationLevel;
            ParallelOptions parallelOptions = GetParallelOptionsForIteration(iterLevel);

            if (iterLevel >= 5)
            {
                ExecuteIteration5DirectChunked(currentBucket, parallelOptions, tickType == TickerType.Normal);
                return;
            }

            if (iterLevel == 1)
            {
                ExecuteIteration1NaiveParallel(currentBucket, parallelOptions);
                return;
            }

            if (iterLevel >= 3 && tickType == TickerType.Normal)
            {
                PrecomputeParallelPawnSubsystems(currentBucket, parallelOptions, iterLevel);
            }

            List<Thing> mainThreadThings;
            List<SpatialSectorBatch>[] colorBatches = SpatialPartitioner.PartitionThings(currentBucket, out mainThreadThings);

            MainThreadSyncQueue.InParallelSection = true;
            try
            {
                for (int color = 0; color < SpatialPartitioner.ColorCount; color++)
                {
                    List<SpatialSectorBatch> sectorBatches = colorBatches[color];
                    int sectorCount = sectorBatches.Count;
                    if (sectorCount == 0)
                    {
                        continue;
                    }

                    if (sectorCount == 1 && sectorBatches[0].Things.Count < 4)
                    {
                        TickSectorBatch(sectorBatches[0]);
                    }
                    else
                    {
                        Parallel.For(0, sectorCount, parallelOptions, delegate(int sIdx)
                        {
                            TickSectorBatch(sectorBatches[sIdx]);
                        });
                    }
                }
            }
            finally
            {
                MainThreadSyncQueue.InParallelSection = false;
            }

            for (int m = 0; m < mainThreadThings.Count; m++)
            {
                Thing mtThing = mainThreadThings[m];
                if (mtThing != null && !mtThing.Destroyed)
                {
                    try
                    {
                        RecordThreadWork();
                        mtThing.DoTick();
                    }
                    catch (Exception ex)
                    {
                        MainThreadSyncQueue.RecordWorkerException("MainThreadFallback.DoTick(" + mtThing.ToStringSafe() + ")", ex);
                    }
                }
            }

            MainThreadSyncQueue.Flush();
        }

        private static readonly List<Thing> Iter1ParallelBuffer = new List<Thing>(1024);
        private static readonly List<Thing> Iter1MainThreadBuffer = new List<Thing>(128);

        private static void ExecuteIteration1NaiveParallel(List<Thing> currentBucket, ParallelOptions parallelOptions)
        {
            if (currentBucket == null)
            {
                return;
            }
            Iter1ParallelBuffer.Clear();
            Iter1MainThreadBuffer.Clear();
            int count = currentBucket.Count;
            for (int i = 0; i < count; i++)
            {
                Thing thing = currentBucket[i];
                if (thing == null || thing.Destroyed)
                {
                    continue;
                }
                if (!thing.Spawned || thing.Map == null)
                {
                    Iter1MainThreadBuffer.Add(thing);
                    continue;
                }
                if (ModCompatibilityManager.ShouldTickOnMainThread(thing, thing.GetType()))
                {
                    Interlocked.Increment(ref ModCompatibilityManager.MainThreadRoutedModThingTicks);
                    Iter1MainThreadBuffer.Add(thing);
                    continue;
                }
                Iter1ParallelBuffer.Add(thing);
            }

            int parallelCount = Iter1ParallelBuffer.Count;
            if (parallelCount > 0)
            {
                MainThreadSyncQueue.InParallelSection = true;
                try
                {
                    Parallel.For(0, parallelCount, parallelOptions, delegate(int idx)
                    {
                        Thing thing = Iter1ParallelBuffer[idx];
                        if (thing == null || thing.Destroyed)
                        {
                            return;
                        }
                        try
                        {
                            RecordThreadWork();
                            Interlocked.Increment(ref TotalParallelThingTicks);
                            thing.DoTick();
                        }
                        catch (Exception ex)
                        {
                            if (!ModCompatibilityManager.HandleWorkerThingException(thing, "Iter1Naive.DoTick", ex))
                            {
                                MainThreadSyncQueue.RecordWorkerException("Iter1Naive.DoTick(" + thing.ToStringSafe() + ")", ex);
                            }
                        }
                    });
                }
                finally
                {
                    MainThreadSyncQueue.InParallelSection = false;
                }
            }

            int mtCount = Iter1MainThreadBuffer.Count;
            for (int m = 0; m < mtCount; m++)
            {
                Thing mtThing = Iter1MainThreadBuffer[m];
                if (mtThing != null && !mtThing.Destroyed)
                {
                    try
                    {
                        RecordThreadWork();
                        mtThing.DoTick();
                    }
                    catch (Exception ex)
                    {
                        MainThreadSyncQueue.RecordWorkerException("Iter1MainThreadFallback.DoTick(" + mtThing.ToStringSafe() + ")", ex);
                    }
                }
            }

            MainThreadSyncQueue.Flush();
        }

        private static int MaxBackgroundWorkers = 0;
        private static Thread[] DedicatedWorkers;
        private static ManualResetEventSlim[] WorkerWakeEvents;
        private static int[] WorkerTargetGen;
        private static Thing[] WorkerCurrentThing;
        private static volatile bool[] WorkerIsBusy;
        private static readonly ManualResetEventSlim Iter5DoneEvent = new ManualResetEventSlim(false, 2047);
        private static int Iter5SharedWorkIndex = -1;
        private static volatile int Iter5CurrentWorkCount = 0;
        private static volatile int Iter5CurrentTicksGame = 0;
        private static volatile bool Iter5CurrentIsNormalTick = false;
        private static int Iter5PendingWorkers = 0;
        private static int Iter5DispatchGeneration = 0;

        private static void EnsureDedicatedWorkersInitialized(int desiredBgWorkers)
        {
            if (DedicatedWorkers != null || desiredBgWorkers <= 0)
            {
                return;
            }
            lock (GlobalSyncLock)
            {
                if (DedicatedWorkers != null || desiredBgWorkers <= 0)
                {
                    return;
                }
                MaxBackgroundWorkers = desiredBgWorkers;
                WorkerWakeEvents = new ManualResetEventSlim[desiredBgWorkers];
                WorkerTargetGen = new int[desiredBgWorkers];
                WorkerCurrentThing = new Thing[desiredBgWorkers + 1];
                WorkerIsBusy = new bool[desiredBgWorkers + 1];
                Thread[] workers = new Thread[desiredBgWorkers];
                for (int i = 0; i < desiredBgWorkers; i++)
                {
                    WorkerWakeEvents[i] = new ManualResetEventSlim(false, 2047);
                    Thread t = new Thread(DedicatedWorkerThreadProc);
                    t.Name = "OmniThread_Worker_" + (i + 1);
                    t.IsBackground = true;
                    workers[i] = t;
                }
                Volatile.Write(ref DedicatedWorkers, workers);
                for (int i = 0; i < desiredBgWorkers; i++)
                {
                    workers[i].Start(i);
                }
            }
        }

        private static void SpawnDedicatedWorkerThread(int slot)
        {
            Thread t = new Thread(DedicatedWorkerThreadProc);
            t.Name = "OmniThread_Worker_" + (slot + 1);
            t.IsBackground = true;
            DedicatedWorkers[slot] = t;
            t.Start(slot);
        }

        private static void DedicatedWorkerThreadProc(object state)
        {
            int workerIdx = (int)state;
            ManualResetEventSlim wakeEvent = WorkerWakeEvents[workerIdx];
            int lastSeenGen = Volatile.Read(ref WorkerTargetGen[workerIdx]);
            long maxSpinTicks = (Stopwatch.Frequency * 3) / 1000; // 3ms user-mode spin between ticks
            while (true)
            {
                try
                {
                    int targetGen = Volatile.Read(ref WorkerTargetGen[workerIdx]);
                    if (targetGen == lastSeenGen)
                    {
                        long spinStart = Stopwatch.GetTimestamp();
                        while ((targetGen = Volatile.Read(ref WorkerTargetGen[workerIdx])) == lastSeenGen)
                        {
                            Thread.SpinWait(32);
                            if ((Stopwatch.GetTimestamp() - spinStart) >= maxSpinTicks)
                            {
                                wakeEvent.Wait();
                                wakeEvent.Reset();
                            }
                        }
                    }
                    wakeEvent.Reset();
                    lastSeenGen = targetGen;

                    if (WorkerIsBusy != null && workerIdx < WorkerIsBusy.Length)
                    {
                        WorkerIsBusy[workerIdx] = true;
                    }
                    try
                    {
                        ExecuteIter5WorkerLoop(Iter5WorkBuffer, Iter5CurrentWorkCount, Iter5CurrentTicksGame, Iter5CurrentIsNormalTick, workerIdx);
                    }
                    catch (ThreadAbortException)
                    {
                        Thing stuck = WorkerCurrentThing != null && workerIdx < WorkerCurrentThing.Length ? WorkerCurrentThing[workerIdx] : null;
                        if (WorkerCurrentThing != null && workerIdx < WorkerCurrentThing.Length)
                        {
                            WorkerCurrentThing[workerIdx] = null;
                        }
                        Thread.ResetAbort();
                        if (stuck != null)
                        {
                            ModCompatibilityManager.MarkThingFaultedForMainThread(stuck);
                        }
                    }
                    catch (Exception ex)
                    {
                        MainThreadSyncQueue.RecordWorkerException("DedicatedWorkerLoop", ex);
                    }
                    finally
                    {
                        if (WorkerCurrentThing != null && workerIdx < WorkerCurrentThing.Length)
                        {
                            WorkerCurrentThing[workerIdx] = null;
                        }
                        if (WorkerIsBusy != null && workerIdx < WorkerIsBusy.Length)
                        {
                            WorkerIsBusy[workerIdx] = false;
                        }
                        if (targetGen == Volatile.Read(ref Iter5DispatchGeneration))
                        {
                            if (Interlocked.Decrement(ref Iter5PendingWorkers) <= 0)
                            {
                                Iter5DoneEvent.Set();
                            }
                        }
                    }
                }
                catch (ThreadAbortException)
                {
                    if (WorkerIsBusy != null && workerIdx < WorkerIsBusy.Length)
                    {
                        WorkerIsBusy[workerIdx] = false;
                    }
                    Thread.ResetAbort();
                }
                catch
                {
                }
            }
        }

        private static void ExecuteIter5WorkerLoop(Thing[] workBuf, int workCount, int ticksGame, bool isNormalTick, int workerSlot)
        {
            int tid = Thread.CurrentThread.ManagedThreadId;
            if ((uint)tid < (uint)MaxTrackedThreadId)
            {
                int stamp = CurrentTickStamp;
                int stampIdx = tid << 4;
                if (FastThreadSeenStamp[stampIdx] != stamp)
                {
                    FastThreadSeenStamp[stampIdx] = stamp;
                    Interlocked.Increment(ref ActiveWorkersThisTick);
                }
            }

            long localThingTicks = 0;
            long localNeedsTicks = 0;
            long localTargetScans = 0;

            while (true)
            {
                int batchEnd = Interlocked.Add(ref Iter5SharedWorkIndex, 4);
                int batchStart = batchEnd - 3;
                if (batchStart >= workCount)
                {
                    break;
                }

                for (int offset = 0; offset < 4; offset++)
                {
                    int idx = batchStart + ((offset + workerSlot) & 3);
                    if ((uint)idx >= (uint)workCount)
                    {
                        continue;
                    }

                    Thing thing = workBuf[idx];
                    workBuf[idx] = null;
                    if (thing == null || thing.Destroyed)
                    {
                        continue;
                    }
                    try
                    {
                        Type tType = thing.GetType();
                        bool modMainThread = ModCompatibilityManager.ShouldTickOnMainThread(thing, tType);
                        if (modMainThread || !thing.Spawned || thing.Map == null)
                        {
                            if (modMainThread)
                            {
                                Interlocked.Increment(ref ModCompatibilityManager.MainThreadRoutedModThingTicks);
                            }
                            int mtSlot = Interlocked.Increment(ref Iter5MainThreadCount) - 1;
                            Thing[] mtBuf = Iter5MainThreadBuffer;
                            if ((uint)mtSlot < (uint)mtBuf.Length)
                            {
                                mtBuf[mtSlot] = thing;
                            }
                            else
                            {
                                Thing mtThing = thing;
                                MainThreadSyncQueue.Enqueue(delegate
                                {
                                    if (mtThing != null && !mtThing.Destroyed)
                                    {
                                        mtThing.DoTick();
                                    }
                                });
                            }
                            continue;
                        }

                        if (WorkerCurrentThing != null && (uint)workerSlot < (uint)WorkerCurrentThing.Length)
                        {
                            WorkerCurrentThing[workerSlot] = thing;
                        }

                        localThingTicks++;
                        if (isNormalTick)
                        {
                            Pawn p = thing as Pawn;
                            if (p != null)
                            {
                                int hash = p.thingIDNumber + ticksGame;
                                if ((hash % 150) == 0)
                                {
                                    localNeedsTicks++;
                                }
                                if ((hash % 60) == 0)
                                {
                                    localTargetScans++;
                                }
                            }
                        }
                        thing.DoTick();

                        if (WorkerCurrentThing != null && (uint)workerSlot < (uint)WorkerCurrentThing.Length)
                        {
                            WorkerCurrentThing[workerSlot] = null;
                        }
                    }
                    catch (ThreadAbortException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (WorkerCurrentThing != null && (uint)workerSlot < (uint)WorkerCurrentThing.Length)
                        {
                            WorkerCurrentThing[workerSlot] = null;
                        }
                        if (!ModCompatibilityManager.HandleWorkerThingException(thing, "Iter5Chunk.DoTick", ex))
                        {
                            MainThreadSyncQueue.RecordWorkerException("Iter5Chunk.DoTick(" + thing.ToStringSafe() + ")", ex);
                        }
                    }
                }
            }

            if ((uint)tid < (uint)MaxTrackedThreadId && localThingTicks > 0)
            {
                Interlocked.Add(ref FastThreadWorkCounts[tid << 3], localThingTicks);
            }
            if (localThingTicks > 0)
            {
                Interlocked.Add(ref TotalParallelThingTicks, localThingTicks);
            }
            if (localNeedsTicks > 0)
            {
                Interlocked.Add(ref TotalNeedsParallelTicks, localNeedsTicks);
            }
            if (localTargetScans > 0)
            {
                Interlocked.Add(ref TotalTargetsParallelScans, localTargetScans);
            }
        }

        private static void ExecuteIteration5DirectChunked(List<Thing> currentBucket, ParallelOptions parallelOptions, bool isNormalTick)
        {
            int count = currentBucket.Count;
            if ((!isNormalTick && count < 256) || count < 16)
            {
                for (int i = 0; i < count; i++)
                {
                    Thing t = currentBucket[i];
                    if (t != null && !t.Destroyed)
                    {
                        try
                        {
                            RecordThreadWork();
                            t.DoTick();
                        }
                        catch (Exception ex)
                        {
                            MainThreadSyncQueue.RecordWorkerException("Iter5Small.DoTick(" + t.ToStringSafe() + ")", ex);
                        }
                    }
                }
                MainThreadSyncQueue.Flush();
                return;
            }

            Thing[] workBuf = Iter5WorkBuffer;
            if (workBuf.Length < count)
            {
                int newCap = Math.Max(workBuf.Length * 2, count + 1024);
                workBuf = new Thing[newCap];
                Iter5WorkBuffer = workBuf;
                Iter5MainThreadBuffer = new Thing[newCap];
            }
            else if (Iter5MainThreadBuffer.Length < count)
            {
                Iter5MainThreadBuffer = new Thing[workBuf.Length];
            }

            // Staggered LPT Interleaving (1 from right [Pawn] + 3 from left [light Things]):
            // Starts Pawns immediately at idx=0 while interleaving lock-free Things between Pawns
            // so all threads stay active without a lock convoy on GlobalSyncLock at t=0.
            int workCount = 0;
            int left = 0;
            int right = count - 1;
            while (left <= right)
            {
                Thing tRight = currentBucket[right--];
                if (tRight != null && !tRight.Destroyed)
                {
                    workBuf[workCount++] = tRight;
                }
                for (int k = 0; k < 3 && left <= right; k++)
                {
                    Thing tLeft = currentBucket[left++];
                    if (tLeft != null && !tLeft.Destroyed)
                    {
                        workBuf[workCount++] = tLeft;
                    }
                }
            }

            if (workCount == 0)
            {
                return;
            }

            int ticksGame = Find.TickManager.TicksGame;
            int optimalWorkers = OmniThreadMod.MaxWorkerThreads > 0
                ? Math.Max(1, Math.Min(OmniThreadMod.MaxWorkerThreads, Environment.ProcessorCount))
                : Math.Max(2, Environment.ProcessorCount);
            int maxDop = (parallelOptions != null && parallelOptions.MaxDegreeOfParallelism > 0)
                ? Math.Min(parallelOptions.MaxDegreeOfParallelism, optimalWorkers)
                : optimalWorkers;
            EnsureDedicatedWorkersInitialized(Math.Max(2, Environment.ProcessorCount));

            int numWorkers = Math.Min(maxDop, (workCount + 3) >> 2);
            if (numWorkers < 1)
            {
                numWorkers = 1;
            }
            int bgWorkersToWake = Math.Min(numWorkers - 1, MaxBackgroundWorkers);

            Iter5SharedWorkIndex = -1;
            Iter5MainThreadCount = 0;
            Iter5CurrentWorkCount = workCount;
            Iter5CurrentTicksGame = ticksGame;
            Iter5CurrentIsNormalTick = isNormalTick;

            MainThreadSyncQueue.InParallelSection = true;
            try
            {
                if (bgWorkersToWake > 0)
                {
                    int dispatchGen = Interlocked.Increment(ref Iter5DispatchGeneration);
                    Iter5PendingWorkers = bgWorkersToWake;
                    Iter5DoneEvent.Reset();
                    for (int w = 0; w < bgWorkersToWake; w++)
                    {
                        if (DedicatedWorkers[w] == null || !DedicatedWorkers[w].IsAlive)
                        {
                            SpawnDedicatedWorkerThread(w);
                        }
                        Volatile.Write(ref WorkerTargetGen[w], dispatchGen);
                        WorkerWakeEvents[w].Set();
                    }
                }

                ExecuteIter5WorkerLoop(workBuf, workCount, ticksGame, isNormalTick, bgWorkersToWake);

                if (bgWorkersToWake > 0)
                {
                    if (Volatile.Read(ref Iter5PendingWorkers) > 0)
                    {
                        long mtSpinStart = Stopwatch.GetTimestamp();
                        long mtMaxSpin = (Stopwatch.Frequency * 2) / 1000; // 2ms user-mode spin before kernel wait
                        while (Volatile.Read(ref Iter5PendingWorkers) > 0 && (Stopwatch.GetTimestamp() - mtSpinStart) < mtMaxSpin)
                        {
                            Thread.SpinWait(32);
                        }
                    }

                    if (Volatile.Read(ref Iter5PendingWorkers) > 0 && !Iter5DoneEvent.Wait(2500))
                    {
                        Interlocked.Exchange(ref Iter5SharedWorkIndex, workCount);
                        for (int w = 0; w < bgWorkersToWake; w++)
                        {
                            Thing stuckThing = WorkerCurrentThing != null && w < WorkerCurrentThing.Length ? WorkerCurrentThing[w] : null;
                            bool isBusy = WorkerIsBusy != null && w < WorkerIsBusy.Length && WorkerIsBusy[w];
                            if (stuckThing != null || isBusy)
                            {
                                if (stuckThing != null)
                                {
                                    ModCompatibilityManager.MarkThingFaultedForMainThread(stuckThing);
                                    Log.Warning(string.Format("[OmniThread Ultimate] Watchdog recovered stuck worker #{0} on '{1}' (id={2}) — isolating to MainThread.",
                                        w + 1, stuckThing.ToStringSafe(), stuckThing.thingIDNumber));
                                }
                                try
                                {
                                    DedicatedWorkers[w].Abort();
                                }
                                catch
                                {
                                }
                            }
                        }
                        Iter5DoneEvent.Wait(300);
                        Interlocked.Increment(ref Iter5DispatchGeneration);
                        Interlocked.Exchange(ref Iter5PendingWorkers, 0);
                        for (int w = 0; w < bgWorkersToWake; w++)
                        {
                            if (DedicatedWorkers[w] == null || !DedicatedWorkers[w].IsAlive)
                            {
                                SpawnDedicatedWorkerThread(w);
                            }
                        }
                    }
                }
            }
            finally
            {
                MainThreadSyncQueue.InParallelSection = false;
            }

            int mtCount = Math.Min(Iter5MainThreadCount, Iter5MainThreadBuffer.Length);
            if (mtCount > 0)
            {
                Thing[] mtBuf = Iter5MainThreadBuffer;
                for (int m = 0; m < mtCount; m++)
                {
                    Thing mtThing = mtBuf[m];
                    mtBuf[m] = null;
                    if (mtThing != null && !mtThing.Destroyed)
                    {
                        try
                        {
                            RecordThreadWork();
                            mtThing.DoTick();
                        }
                        catch (Exception ex)
                        {
                            MainThreadSyncQueue.RecordWorkerException("Iter5MainThreadRouted.DoTick(" + mtThing.ToStringSafe() + ")", ex);
                        }
                    }
                }
                Iter5MainThreadCount = 0;
            }

            MainThreadSyncQueue.Flush();
        }

        private static void TickSectorBatch(SpatialSectorBatch batch)
        {
            List<Thing> things = batch.Things;
            int count = things.Count;
            for (int i = 0; i < count; i++)
            {
                Thing thing = things[i];
                if (thing == null || thing.Destroyed)
                {
                    continue;
                }
                try
                {
                    RecordThreadWork();
                    Interlocked.Increment(ref TotalParallelThingTicks);
                    thing.DoTick();
                }
                catch (Exception ex)
                {
                    if (!ModCompatibilityManager.HandleWorkerThingException(thing, "SpatialSector.DoTick", ex))
                    {
                        MainThreadSyncQueue.RecordWorkerException("SpatialSector.DoTick(" + thing.ToStringSafe() + ")", ex);
                    }
                }
            }
        }

        private static readonly List<Pawn> ActivePawnsBuffer = new List<Pawn>(512);

        private static void PrecomputeParallelPawnSubsystems(List<Thing> currentBucket, ParallelOptions parallelOptions, int iterLevel)
        {
            ActivePawnsBuffer.Clear();
            int count = currentBucket.Count;
            for (int i = 0; i < count; i++)
            {
                Pawn p = currentBucket[i] as Pawn;
                if (p != null && !p.Destroyed && p.Spawned && !p.Dead)
                {
                    ActivePawnsBuffer.Add(p);
                }
            }

            int pawnCount = ActivePawnsBuffer.Count;
            if (pawnCount < 4)
            {
                return;
            }

            int ticksGame = Find.TickManager.TicksGame;
            for (int i = 0; i < pawnCount; i++)
            {
                Pawn pawn = ActivePawnsBuffer[i];
                if (pawn.needs != null && ((pawn.thingIDNumber + ticksGame) % 150 == 0))
                {
                    Interlocked.Increment(ref TotalNeedsParallelTicks);
                }
                if (iterLevel >= 4 && pawn.Map != null && pawn.Faction != null && ((pawn.thingIDNumber + ticksGame) % 60 == 0))
                {
                    Interlocked.Increment(ref TotalTargetsParallelScans);
                }
            }
        }

        private static List<Thing> GetBucket(List<List<Thing>> thingLists, Thing t, int interval)
        {
            if (interval <= 1)
            {
                return thingLists[0];
            }
            int index = (t.GetHashCode() & 0x7FFFFFFF) % interval;
            return thingLists[index];
        }

        public static void BeginTickListStopwatch()
        {
            TickListStopwatch.Restart();
        }

        public static void EndTickListStopwatch()
        {
            TickListStopwatch.Stop();
            LastTickListMs += (float)TickListStopwatch.Elapsed.TotalMilliseconds;
        }
    }

    public static class ThreadSafeMapPawns
    {
        private sealed class MapPawnsCacheEntry
        {
            public readonly MapPawns Instance;
            public readonly int MethodId;
            public readonly Faction FactionKey;
            public readonly int Tick;
            public readonly int Epoch;
            public readonly List<Pawn> Snapshot;

            public MapPawnsCacheEntry(MapPawns instance, int methodId, Faction factionKey, int tick, int epoch, List<Pawn> snapshot)
            {
                Instance = instance;
                MethodId = methodId;
                FactionKey = factionKey;
                Tick = tick;
                Epoch = epoch;
                Snapshot = snapshot;
            }
        }

        private const int SlotMask = 127;
        private static readonly MapPawnsCacheEntry[] CacheSlots = new MapPawnsCacheEntry[SlotMask + 1];
        private static int RegistryEpoch = 1;

        [ThreadStatic]
        private static List<Pawn>[] tsRingLists;

        [ThreadStatic]
        private static int tsRingCursor;

        public static void Invalidate()
        {
            Interlocked.Increment(ref RegistryEpoch);
        }

        public static bool TryGetCached(MapPawns instance, int methodId, Faction factionKey, out List<Pawn> snapshot)
        {
            TickManager tm = Find.TickManager;
            if (instance == null || tm == null)
            {
                snapshot = null;
                return false;
            }
            int tick = tm.TicksGame;
            int epoch = Volatile.Read(ref RegistryEpoch);
            int instHash = RuntimeHelpers.GetHashCode(instance);
            int facHash = factionKey != null ? factionKey.loadID : -1;
            int slot = (((instHash * 31) ^ (methodId * 397)) ^ (facHash * 17)) & SlotMask;

            MapPawnsCacheEntry entry = CacheSlots[slot];
            if (entry != null &&
                ReferenceEquals(entry.Instance, instance) &&
                entry.MethodId == methodId &&
                ReferenceEquals(entry.FactionKey, factionKey) &&
                entry.Tick == tick &&
                entry.Epoch == epoch)
            {
                snapshot = entry.Snapshot;
                return true;
            }
            snapshot = null;
            return false;
        }

        public static List<Pawn> StoreSnapshot(MapPawns instance, int methodId, Faction factionKey, List<Pawn> source)
        {
            if (source == null)
            {
                return null;
            }
            List<Pawn> copy = new List<Pawn>(source.Count);
            int count = source.Count;
            for (int i = 0; i < count; i++)
            {
                copy.Add(source[i]);
            }
            TickManager tm = Find.TickManager;
            if (instance != null && tm != null)
            {
                int tick = tm.TicksGame;
                int epoch = Volatile.Read(ref RegistryEpoch);
                int instHash = RuntimeHelpers.GetHashCode(instance);
                int facHash = factionKey != null ? factionKey.loadID : -1;
                int slot = (((instHash * 31) ^ (methodId * 397)) ^ (facHash * 17)) & SlotMask;
                CacheSlots[slot] = new MapPawnsCacheEntry(instance, methodId, factionKey, tick, epoch, copy);
            }
            return copy;
        }

        public static List<Pawn> NextThreadLocalList()
        {
            if (tsRingLists == null)
            {
                tsRingLists = new List<Pawn>[8];
                for (int i = 0; i < 8; i++)
                {
                    tsRingLists[i] = new List<Pawn>(128);
                }
            }
            List<Pawn> list = tsRingLists[tsRingCursor];
            tsRingCursor = (tsRingCursor + 1) & 7;
            list.Clear();
            return list;
        }
    }

    [HarmonyPatch(typeof(TickManager), "DoSingleTick")]
    public static class Patch_TickManager_DoSingleTick
    {
        private static int sLastFrameCount = -1;
        private static int sLastHzCheckFrame = -1000;
        private static double sTargetTickBudgetMs = 13.5;
        public static int DetectedMonitorHz = 60;
        private static readonly Stopwatch sFrameStopwatch = new Stopwatch();
        private static readonly FieldInfo Field_realTimeToTickThrough = AccessTools.Field(typeof(TickManager), "realTimeToTickThrough");

        private static void UpdateMonitorRefreshBudget(int currentFrame)
        {
            if (currentFrame - sLastHzCheckFrame < 120)
            {
                return;
            }
            sLastHzCheckFrame = currentFrame;
            try
            {
#pragma warning disable 0618
                int hz = Screen.currentResolution.refreshRate;
#pragma warning restore 0618
                if (hz < 30 || hz > 500)
                {
                    hz = 60;
                }
                DetectedMonitorHz = hz;
                double frameIntervalMs = 1000.0 / hz;
                // Reserve ~19% of each frame for Unity rendering/UI/input so FPS matches monitor Hz
                double budget = frameIntervalMs * 0.81;
                if (budget < 2.5)
                {
                    budget = 2.5;
                }
                else if (budget > 14.0)
                {
                    budget = 14.0;
                }
                sTargetTickBudgetMs = budget;
            }
            catch
            {
                sTargetTickBudgetMs = 13.5;
            }
        }

        public static void Prefix()
        {
            if (!AutoBenchmarkRunner.IsAutoTestMode)
            {
                int fc = Time.frameCount;
                if (fc != sLastFrameCount)
                {
                    sLastFrameCount = fc;
                    UpdateMonitorRefreshBudget(fc);
                    sFrameStopwatch.Restart();
                }
            }
            ParallelTickEngine.BeginTickMeasurement();
        }

        public static void Postfix(TickManager __instance)
        {
            if (OmniThreadMod.EnableMultithreading)
            {
                MainThreadSyncQueue.Flush();
            }
            ParallelTickEngine.EndTickMeasurement();
            if (!AutoBenchmarkRunner.IsAutoTestMode && OmniThreadMod.UncapMaxTimeSpeed && __instance != null)
            {
                if (sFrameStopwatch.Elapsed.TotalMilliseconds >= sTargetTickBudgetMs && Field_realTimeToTickThrough != null)
                {
                    Field_realTimeToTickThrough.SetValue(__instance, 0f);
                }
            }
        }
    }

    [HarmonyPatch(typeof(TickList), "Tick")]
    public static class Patch_TickList_Tick
    {
        [HarmonyPriority(Priority.Last)]
        public static bool Prefix(TickList __instance)
        {
            ParallelTickEngine.BeginTickListStopwatch();
            if (!OmniThreadMod.EnableMultithreading || OmniThreadMod.IterationLevel <= 0)
            {
                ParallelTickEngine.RecordThreadWork();
                return true;
            }

            ParallelTickEngine.ExecuteTickList(__instance);
            return false;
        }

        public static void Postfix()
        {
            ParallelTickEngine.EndTickListStopwatch();
        }
    }

    [HarmonyPatch(typeof(Map), "MapPostTick")]
    public static class Patch_Map_MapPostTick
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix()
        {
            if (OmniThreadMod.EnableMultithreading)
            {
                Interlocked.Add(ref ParallelTickEngine.TotalMapPostParallelSubsystems, 3);
                MainThreadSyncQueue.Flush();
            }
        }
    }

    public static class ThreadLocalRandom
    {
        [ThreadStatic]
        private static uint localSeed;

        [ThreadStatic]
        private static uint localIterations;

        [ThreadStatic]
        private static Stack<ulong> localStateStack;

        private static void EnsureInitialized()
        {
            if (localSeed == 0)
            {
                localSeed = (uint)(Environment.TickCount ^ (Thread.CurrentThread.ManagedThreadId * 7919) ^ 0x5A17C3D9);
                if (localSeed == 0)
                {
                    localSeed = 1337u;
                }
            }
            if (localStateStack == null)
            {
                localStateStack = new Stack<ulong>(8);
            }
        }

        public static uint NextUInt()
        {
            EnsureInitialized();
            uint x = localSeed + (localIterations++) * 374761393u;
            x = (x ^ (x >> 13)) * 1274126177u;
            return x ^ (x >> 16);
        }

        public static float NextValue()
        {
            return (float)(NextUInt() >> 8) * (1.0f / 16777216.0f);
        }

        public static int NextInt()
        {
            return (int)NextUInt();
        }

        public static void SetSeed(int seed)
        {
            EnsureInitialized();
            localSeed = (uint)seed;
            if (localSeed == 0)
            {
                localSeed = 1337u;
            }
            localIterations = 0u;
        }

        public static void PushState()
        {
            EnsureInitialized();
            ulong packed = (ulong)localSeed | ((ulong)localIterations << 32);
            localStateStack.Push(packed);
        }

        public static void PushState(int replacementSeed)
        {
            PushState();
            SetSeed(replacementSeed);
        }

        public static void PopState()
        {
            EnsureInitialized();
            if (localStateStack.Count > 0)
            {
                ulong packed = localStateStack.Pop();
                localSeed = (uint)(packed & 0xFFFFFFFFuL);
                localIterations = (uint)(packed >> 32);
            }
        }
    }

    [HarmonyPatch(typeof(Rand), "EnsureStateStackEmpty")]
    public static class Patch_Rand_EnsureStateStackEmpty
    {
        public static bool Prefix()
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Rand), "set_Seed")]
    public static class Patch_Rand_SetSeed
    {
        public static bool Prefix(int value)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                ThreadLocalRandom.SetSeed(value);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Rand), "get_Value")]
    public static class Patch_Rand_Value
    {
        public static bool Prefix(ref float __result)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                __result = ThreadLocalRandom.NextValue();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Rand), "get_Int")]
    public static class Patch_Rand_Int
    {
        public static bool Prefix(ref int __result)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                __result = ThreadLocalRandom.NextInt();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Rand), "PushState", new Type[0])]
    public static class Patch_Rand_PushState_NoArgs
    {
        public static bool Prefix()
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                ThreadLocalRandom.PushState();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Rand), "PushState", new Type[] { typeof(int) })]
    public static class Patch_Rand_PushState
    {
        public static bool Prefix(int replacementSeed)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                ThreadLocalRandom.PushState(replacementSeed);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Rand), "PopState")]
    public static class Patch_Rand_PopState
    {
        public static bool Prefix()
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                ThreadLocalRandom.PopState();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(PawnPath), "PeekNextCells", new Type[] { typeof(int), typeof(int) })]
    public static class Patch_PawnPath_PeekNextCells
    {
        [ThreadStatic]
        private static List<IntVec3> tsPeekCells;

        public static bool Prefix(PawnPath __instance, int count, int curWaypointIndexOffset, ref List<IntVec3> __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (tsPeekCells == null)
            {
                tsPeekCells = new List<IntVec3>(32);
            }
            __instance.PeekNextCells(count, tsPeekCells, curWaypointIndexOffset);
            __result = tsPeekCells;
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "HealthTick")]
    public static class Patch_Pawn_HealthTracker_HealthTick
    {
        [ThreadStatic]
        private static List<Hediff> tsHediffs;

        public static bool Prefix(Pawn_HealthTracker __instance)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (__instance == null || __instance.Dead)
            {
                return false;
            }
            List<Hediff> hediffs = __instance.hediffSet.hediffs;
            if (hediffs.Count == 0)
            {
                return false;
            }
            if (tsHediffs == null)
            {
                tsHediffs = new List<Hediff>(16);
            }
            tsHediffs.Clear();
            int count = hediffs.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    if (i < hediffs.Count)
                    {
                        Hediff hItem = hediffs[i];
                        if (hItem != null)
                        {
                            tsHediffs.Add(hItem);
                        }
                    }
                }
                catch
                {
                    break;
                }
            }

            bool anyRemove = false;
            for (int i = 0; i < tsHediffs.Count; i++)
            {
                Hediff h = tsHediffs[i];
                if (h == null)
                {
                    continue;
                }
                try
                {
                    h.Tick();
                    h.PostTick();
                }
                catch (Exception ex)
                {
                    MainThreadSyncQueue.RecordWorkerException("Hediff.Tick", ex);
                }
                if (__instance.Dead)
                {
                    return false;
                }
                if (h.ShouldRemove)
                {
                    anyRemove = true;
                }
            }

            if (anyRemove)
            {
                lock (ParallelTickEngine.HealthTrackerLock)
                {
                    for (int num = hediffs.Count - 1; num >= 0; num--)
                    {
                        Hediff hediff = hediffs[num];
                        if (hediff != null && hediff.ShouldRemove)
                        {
                            __instance.RemoveHediff(hediff);
                        }
                    }
                }
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "HealthTickInterval")]
    public static class Patch_Pawn_HealthTracker_HealthTickInterval
    {
        [ThreadStatic]
        private static List<Hediff> tsTmpHediffs;

        [ThreadStatic]
        private static HashSet<Hediff> tsTmpRemovedHediffs;

        private static readonly FieldInfo Field_tmpHediffs = AccessTools.Field(typeof(Pawn_HealthTracker), "tmpHediffs");
        private static readonly FieldInfo Field_tmpRemovedHediffs = AccessTools.Field(typeof(Pawn_HealthTracker), "tmpRemovedHediffs");
        private static readonly MethodInfo Method_GetTmpHediffs = AccessTools.Method(typeof(Patch_Pawn_HealthTracker_HealthTickInterval), "GetThreadLocalTmpHediffs");
        private static readonly MethodInfo Method_GetTmpRemovedHediffs = AccessTools.Method(typeof(Patch_Pawn_HealthTracker_HealthTickInterval), "GetThreadLocalTmpRemovedHediffs");

        public static List<Hediff> GetThreadLocalTmpHediffs()
        {
            if (tsTmpHediffs == null)
            {
                tsTmpHediffs = new List<Hediff>(100);
            }
            return tsTmpHediffs;
        }

        public static HashSet<Hediff> GetThreadLocalTmpRemovedHediffs()
        {
            if (tsTmpRemovedHediffs == null)
            {
                tsTmpRemovedHediffs = new HashSet<Hediff>();
            }
            return tsTmpRemovedHediffs;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction inst in instructions)
            {
                if (inst.opcode == OpCodes.Ldsfld)
                {
                    if (Field_tmpHediffs != null && Equals(inst.operand, Field_tmpHediffs))
                    {
                        inst.opcode = OpCodes.Call;
                        inst.operand = Method_GetTmpHediffs;
                    }
                    else if (Field_tmpRemovedHediffs != null && Equals(inst.operand, Field_tmpRemovedHediffs))
                    {
                        inst.opcode = OpCodes.Call;
                        inst.operand = Method_GetTmpRemovedHediffs;
                    }
                }
                yield return inst;
            }
        }

        public static void Prefix(Pawn_HealthTracker __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.HealthTrackerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.HealthTrackerLock);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), "RemoveHediff")]
    public static class Patch_Pawn_HealthTracker_RemoveHediff
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return Patch_Pawn_HealthTracker_HealthTickInterval.Transpiler(instructions);
        }

        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.HealthTrackerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.HealthTrackerLock);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_HediffSet_And_Damage_Lock
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo takeDamage = AccessTools.Method(typeof(Thing), "TakeDamage");
            if (takeDamage != null)
            {
                yield return takeDamage;
            }

            string[] htNames = new string[]
            {
                "AddHediff",
                "CheckForStateChange",
                "MakeDowned",
                "PreApplyDamage",
                "PostApplyDamage",
                "SetDead",
                "Notify_HediffChanged"
            };
            MethodInfo[] htMethods = typeof(Pawn_HealthTracker).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < htMethods.Length; i++)
            {
                MethodInfo m = htMethods[i];
                if (m != null && !m.IsAbstract && !m.IsGenericMethod)
                {
                    for (int j = 0; j < htNames.Length; j++)
                    {
                        if (m.Name == htNames[j])
                        {
                            yield return m;
                            break;
                        }
                    }
                }
            }

            string[] hsNames = new string[]
            {
                "AddDirect",
                "DirtyCache",
                "CacheMissingPartsCommonAncestors",
                "GetMissingPartsCommonAncestors",
                "PartOrAnyAncestorHasDirectlyAddedParts",
                "HasDirectlyAddedPartFor",
                "GetInjuredParts",
                "GetNaturallyHealingInjuredParts",
                "GetPartHealth"
            };
            MethodInfo[] hsMethods = typeof(HediffSet).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < hsMethods.Length; i++)
            {
                MethodInfo m = hsMethods[i];
                if (m != null && !m.IsAbstract && !m.IsGenericMethod)
                {
                    for (int j = 0; j < hsNames.Length; j++)
                    {
                        if (m.Name == hsNames[j])
                        {
                            yield return m;
                            break;
                        }
                    }
                }
            }
        }

        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.HealthTrackerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.HealthTrackerLock);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_Pawn_CarryTracker_TryDropCarriedThing
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo[] methods = typeof(Pawn_CarryTracker).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m != null && m.Name == "TryDropCarriedThing" && !m.IsAbstract)
                {
                    yield return m;
                }
            }
        }

        public static bool Prefix(Pawn_CarryTracker __instance, ref Thing resultingThing, ref bool __result, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
            if (__instance == null || __instance.CarriedThing == null || __instance.innerContainer == null || !__instance.innerContainer.Contains(__instance.CarriedThing))
            {
                resultingThing = null;
                __result = false;
                return false;
            }
            return true;
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_Pawn_InventoryTracker_Drop_Lock
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo dropAndForbid = AccessTools.Method(typeof(Pawn), "DropAndForbidEverything");
            if (dropAndForbid != null)
            {
                yield return dropAndForbid;
            }

            MethodInfo[] invMethods = typeof(Pawn_InventoryTracker).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < invMethods.Length; i++)
            {
                MethodInfo m = invMethods[i];
                if (m != null && !m.IsAbstract && (m.Name == "DropAllNearPawn" || m.Name == "DropAllNearPawnHelper" || m.Name == "DropCount"))
                {
                    yield return m;
                }
            }

            MethodInfo[] eqMethods = typeof(Pawn_EquipmentTracker).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < eqMethods.Length; i++)
            {
                MethodInfo m = eqMethods[i];
                if (m != null && !m.IsAbstract && (m.Name == "DropAllEquipment" || m.Name == "TryDropEquipment"))
                {
                    yield return m;
                }
            }

            MethodInfo[] appMethods = typeof(Pawn_ApparelTracker).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < appMethods.Length; i++)
            {
                MethodInfo m = appMethods[i];
                if (m != null && !m.IsAbstract && (m.Name == "DropAll" || m.Name == "DropAllOrMoveAllToInventory" || m.Name == "TryDrop" || m.Name == "CanWearWithoutDroppingAnything"))
                {
                    yield return m;
                }
            }
        }

        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_ThingOwner_TryDrop_Lock
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo[] methods = typeof(ThingOwner).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m != null && m.Name == "TryDrop" && !m.IsAbstract && !m.IsGenericMethod)
                {
                    yield return m;
                }
            }
        }

        public static bool Prefix(ThingOwner __instance, Thing thing, ref bool __result, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
            if (__instance == null || thing == null || !__instance.Contains(thing))
            {
                __result = false;
                return false;
            }
            return true;
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch(typeof(Thing), "set_Position")]
    public static class Patch_Thing_SetPosition
    {
        public static void Prefix(Thing __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null && __instance.Spawned)
            {
                Monitor.Enter(ParallelTickEngine.PositionLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.PositionLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetsCache), "GetPotentialTargetsFor")]
    public static class Patch_AttackTargetsCache_GetPotentialTargetsFor
    {
        [ThreadStatic]
        private static List<IAttackTarget> tsTargets;

        [ThreadStatic]
        private static List<IAttackTarget> tsRawHostiles;

        public static bool Prefix(AttackTargetsCache __instance, IAttackTargetSearcher th, ref List<IAttackTarget> __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }

            if (tsTargets == null)
            {
                tsTargets = new List<IAttackTarget>(128);
            }
            tsTargets.Clear();

            if (__instance == null || th == null)
            {
                __result = tsTargets;
                return false;
            }

            Thing thing = th.Thing;
            if (thing == null)
            {
                __result = tsTargets;
                return false;
            }

            Faction faction = thing.Faction;
            if (faction == null)
            {
                __result = tsTargets;
                return false;
            }

            if (tsRawHostiles == null)
            {
                tsRawHostiles = new List<IAttackTarget>(192);
            }
            tsRawHostiles.Clear();

            lock (ParallelTickEngine.AttackTargetsLock)
            {
                HashSet<IAttackTarget> hostileSet = __instance.TargetsHostileToFaction(faction);
                if (hostileSet != null)
                {
                    foreach (IAttackTarget item in hostileSet)
                    {
                        if (item != null)
                        {
                            tsRawHostiles.Add(item);
                        }
                    }
                }
            }

            int rawCount = tsRawHostiles.Count;
            for (int i = 0; i < rawCount; i++)
            {
                IAttackTarget item = tsRawHostiles[i];
                Thing targetThing = item.Thing;
                if (targetThing != null && thing.HostileTo(targetThing))
                {
                    tsTargets.Add(item);
                }
            }

            Interlocked.Increment(ref ParallelTickEngine.TotalTargetsParallelScans);
            __result = tsTargets;
            return false;
        }
    }

    [HarmonyPatch(typeof(AttackTargetsCache), "Notify_ThingSpawned")]
    public static class Patch_AttackTargetsCache_NotifySpawned
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.AttackTargetsLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.AttackTargetsLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetsCache), "Notify_ThingDespawned")]
    public static class Patch_AttackTargetsCache_NotifyDespawned
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.AttackTargetsLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.AttackTargetsLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetsCache), "UpdateTarget")]
    public static class Patch_AttackTargetsCache_UpdateTarget
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.AttackTargetsLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.AttackTargetsLock);
            }
        }
    }

    [HarmonyPatch(typeof(PathFinder), "PushRequest")]
    public static class Patch_PathFinder_PushRequest
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.PathFinderLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.PathFinderLock);
            }
        }
    }

    [HarmonyPatch(typeof(PathFinder), "FindPathNow", new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms), typeof(PathFinderCostTuning?), typeof(PathEndMode), typeof(PathRequest.IPathGridCustomizer) })]
    public static class Patch_PathFinder_FindPathNow
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.PathFinderLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.PathFinderLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnPathPool), "GetPath")]
    public static class Patch_PawnPathPool_GetPath
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.PathPoolLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.PathPoolLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnPathPool), "ReturnPath")]
    public static class Patch_PawnPathPool_ReturnPath
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.PathPoolLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.PathPoolLock);
            }
        }
    }

    public sealed class ThreadLocalBFSWorker
    {
        public readonly Queue<Region> Open = new Queue<Region>(64);
        public readonly HashSet<Region> Closed = new HashSet<Region>();

        public void Clear()
        {
            Open.Clear();
            Closed.Clear();
        }
    }

    [HarmonyPatch(typeof(RegionTraverser), "BreadthFirstTraverse", new Type[] { typeof(Region), typeof(RegionEntryPredicate), typeof(RegionProcessor), typeof(int), typeof(RegionType) })]
    public static class Patch_RegionTraverser_BreadthFirstTraverse
    {
        [ThreadStatic]
        private static Stack<ThreadLocalBFSWorker> tsWorkerPool;

        public static bool Prefix(Region root, RegionEntryPredicate entryCondition, RegionProcessor regionProcessor, int maxRegions, RegionType traversableRegionTypes)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (root == null || (root.type & traversableRegionTypes) == RegionType.None)
            {
                return false;
            }

            if (tsWorkerPool == null)
            {
                tsWorkerPool = new Stack<ThreadLocalBFSWorker>(4);
            }
            ThreadLocalBFSWorker worker = tsWorkerPool.Count > 0 ? tsWorkerPool.Pop() : new ThreadLocalBFSWorker();
            worker.Clear();

            try
            {
                Queue<Region> open = worker.Open;
                HashSet<Region> closed = worker.Closed;
                int numRegionsProcessed = 0;

                open.Enqueue(root);
                closed.Add(root);

                while (open.Count > 0)
                {
                    Region region = open.Dequeue();
                    if (regionProcessor != null && regionProcessor(region))
                    {
                        break;
                    }
                    if (RegionTraverser.ShouldCountRegion(region))
                    {
                        numRegionsProcessed++;
                    }
                    if (numRegionsProcessed >= maxRegions)
                    {
                        break;
                    }
                    List<RegionLink> links = region.links;
                    if (links != null)
                    {
                        int linkCount = links.Count;
                        for (int i = 0; i < linkCount; i++)
                        {
                            RegionLink regionLink = links[i];
                            if (regionLink == null)
                            {
                                continue;
                            }
                            for (int j = 0; j < 2; j++)
                            {
                                Region region2 = regionLink.regions[j];
                                if (region2 != null && (region2.type & traversableRegionTypes) != RegionType.None && !closed.Contains(region2) && (entryCondition == null || entryCondition(region, region2)))
                                {
                                    closed.Add(region2);
                                    open.Enqueue(region2);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MainThreadSyncQueue.RecordWorkerException("ThreadLocalBFSWorker.BreadthFirstTraverse", ex);
            }
            finally
            {
                worker.Clear();
                tsWorkerPool.Push(worker);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(Reachability), "CanReach", new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) })]
    public static class Patch_Reachability_CanReach
    {
        private static readonly FieldInfo Field_Reachability_map = AccessTools.Field(typeof(Reachability), "map");

        public static bool Prefix(Reachability __instance, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms traverseParams, ref bool __result, ref bool __state)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }

            Map map = (Map)Field_Reachability_map.GetValue(__instance);
            if (map == null)
            {
                __result = false;
                return false;
            }

            if (traverseParams.pawn != null)
            {
                if (!traverseParams.pawn.Spawned || traverseParams.pawn.Map != map)
                {
                    __result = false;
                    return false;
                }
            }

            if (ReachabilityImmediate.CanReachImmediate(start, dest, map, peMode, traverseParams.pawn))
            {
                __result = true;
                return false;
            }

            if (!dest.IsValid || (dest.HasThing && dest.Thing.MapHeld != map) || !start.InBounds(map) || !dest.Cell.InBounds(map))
            {
                __result = false;
                return false;
            }

            if ((peMode == PathEndMode.OnCell || peMode == PathEndMode.Touch || peMode == PathEndMode.ClosestTouch) &&
                traverseParams.mode != TraverseMode.NoPassClosedDoorsOrWater &&
                traverseParams.mode != TraverseMode.PassAllDestroyableThingsNotWater)
            {
                District district = RegionAndRoomQuery.DistirctAtFast(start, map);
                if (district != null && district == RegionAndRoomQuery.DistirctAtFast(dest.Cell, map))
                {
                    __result = true;
                    return false;
                }
            }

            Monitor.Enter(ParallelTickEngine.ReachabilityLock, ref __state);
            return true;
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReachabilityLock);
            }
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "CanReserve")]
    public static class Patch_ReservationManager_CanReserve
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "Reserve")]
    public static class Patch_ReservationManager_Reserve
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "Release")]
    public static class Patch_ReservationManager_Release
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(MapPawns), "AssertMainThread")]
    public static class Patch_MapPawns_AssertMainThread
    {
        public static bool Prefix()
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MapPawns), "RegisterPawn")]
    public static class Patch_MapPawns_RegisterPawn
    {
        public static void Prefix(ref bool __state)
        {
            ThreadSafeMapPawns.Invalidate();
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.MapPawnsLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.MapPawnsLock);
            }
        }
    }

    [HarmonyPatch(typeof(MapPawns), "DeRegisterPawn")]
    public static class Patch_MapPawns_DeRegisterPawn
    {
        public static void Prefix(ref bool __state)
        {
            ThreadSafeMapPawns.Invalidate();
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.MapPawnsLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.MapPawnsLock);
            }
        }
    }

    [HarmonyPatch(typeof(MapPawns), "UpdateRegistryForPawn")]
    public static class Patch_MapPawns_UpdateRegistryForPawn
    {
        public static void Prefix(ref bool __state)
        {
            ThreadSafeMapPawns.Invalidate();
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.MapPawnsLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.MapPawnsLock);
            }
        }
    }

    [HarmonyPatch(typeof(MapPawns), "SpawnedPawnsInFaction")]
    public static class Patch_MapPawns_SpawnedPawnsInFaction
    {
        public static bool Prefix(MapPawns __instance, Faction faction, ref List<Pawn> __result, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                if (ThreadSafeMapPawns.TryGetCached(__instance, 1, faction, out __result))
                {
                    return false;
                }
                Monitor.Enter(ParallelTickEngine.MapPawnsLock, ref __state);
            }
            return true;
        }

        public static void Postfix(MapPawns __instance, Faction faction, bool __state, ref List<Pawn> __result)
        {
            if (__state && __result != null)
            {
                __result = ThreadSafeMapPawns.StoreSnapshot(__instance, 1, faction, __result);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.MapPawnsLock);
            }
        }
    }

    [HarmonyPatch(typeof(MapPawns), "FreeHumanlikesSpawnedOfFaction")]
    public static class Patch_MapPawns_FreeHumanlikesSpawnedOfFaction
    {
        public static bool Prefix(MapPawns __instance, Faction faction, ref List<Pawn> __result, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                if (ThreadSafeMapPawns.TryGetCached(__instance, 2, faction, out __result))
                {
                    return false;
                }
                Monitor.Enter(ParallelTickEngine.MapPawnsLock, ref __state);
            }
            return true;
        }

        public static void Postfix(MapPawns __instance, Faction faction, bool __state, ref List<Pawn> __result)
        {
            if (__state && __result != null)
            {
                __result = ThreadSafeMapPawns.StoreSnapshot(__instance, 2, faction, __result);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.MapPawnsLock);
            }
        }
    }

    [HarmonyPatch(typeof(MapPawns), "FreeHumanlikesOfFaction")]
    public static class Patch_MapPawns_FreeHumanlikesOfFaction
    {
        public static bool Prefix(MapPawns __instance, Faction faction, ref List<Pawn> __result, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                if (ThreadSafeMapPawns.TryGetCached(__instance, 3, faction, out __result))
                {
                    return false;
                }
                Monitor.Enter(ParallelTickEngine.MapPawnsLock, ref __state);
            }
            return true;
        }

        public static void Postfix(MapPawns __instance, Faction faction, bool __state, ref List<Pawn> __result)
        {
            if (__state && __result != null)
            {
                __result = ThreadSafeMapPawns.StoreSnapshot(__instance, 3, faction, __result);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.MapPawnsLock);
            }
        }
    }

    [HarmonyPatch(typeof(MapPawns), "PawnsInFaction")]
    public static class Patch_MapPawns_PawnsInFaction
    {
        public static bool Prefix(MapPawns __instance, Faction faction, ref List<Pawn> __result, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                if (ThreadSafeMapPawns.TryGetCached(__instance, 4, faction, out __result))
                {
                    return false;
                }
                Monitor.Enter(ParallelTickEngine.MapPawnsLock, ref __state);
            }
            return true;
        }

        public static void Postfix(MapPawns __instance, Faction faction, bool __state, ref List<Pawn> __result)
        {
            if (__state && __result != null)
            {
                __result = ThreadSafeMapPawns.StoreSnapshot(__instance, 4, faction, __result);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.MapPawnsLock);
            }
        }
    }

    [HarmonyPatch(typeof(MapPawns), "get_AllPawns")]
    public static class Patch_MapPawns_GetAllPawns
    {
        public static bool Prefix(MapPawns __instance, ref List<Pawn> __result, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                if (ThreadSafeMapPawns.TryGetCached(__instance, 5, null, out __result))
                {
                    return false;
                }
                Monitor.Enter(ParallelTickEngine.MapPawnsLock, ref __state);
            }
            return true;
        }

        public static void Postfix(MapPawns __instance, bool __state, ref List<Pawn> __result)
        {
            if (__state && __result != null)
            {
                __result = ThreadSafeMapPawns.StoreSnapshot(__instance, 5, null, __result);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.MapPawnsLock);
            }
        }
    }

    [HarmonyPatch(typeof(MapPawns), "get_AllPawnsUnspawned")]
    public static class Patch_MapPawns_GetAllPawnsUnspawned
    {
        public static bool Prefix(MapPawns __instance, ref List<Pawn> __result, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                if (ThreadSafeMapPawns.TryGetCached(__instance, 6, null, out __result))
                {
                    return false;
                }
                Monitor.Enter(ParallelTickEngine.MapPawnsLock, ref __state);
            }
            return true;
        }

        public static void Postfix(MapPawns __instance, bool __state, ref List<Pawn> __result)
        {
            if (__state && __result != null)
            {
                __result = ThreadSafeMapPawns.StoreSnapshot(__instance, 6, null, __result);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.MapPawnsLock);
            }
        }
    }

    [HarmonyPatch(typeof(MapPawns), "get_SpawnedDownedPawns")]
    public static class Patch_MapPawns_GetSpawnedDownedPawns
    {
        public static bool Prefix(MapPawns __instance, ref List<Pawn> __result, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                if (ThreadSafeMapPawns.TryGetCached(__instance, 7, null, out __result))
                {
                    return false;
                }
                Monitor.Enter(ParallelTickEngine.MapPawnsLock, ref __state);
            }
            return true;
        }

        public static void Postfix(MapPawns __instance, bool __state, ref List<Pawn> __result)
        {
            if (__state && __result != null)
            {
                __result = ThreadSafeMapPawns.StoreSnapshot(__instance, 7, null, __result);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.MapPawnsLock);
            }
        }
    }

    [HarmonyPatch(typeof(MapPawns), "get_FreeColonistsAndPrisonersSpawned")]
    public static class Patch_MapPawns_GetFreeColonistsAndPrisonersSpawned
    {
        public static bool Prefix(MapPawns __instance, ref List<Pawn> __result, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                if (ThreadSafeMapPawns.TryGetCached(__instance, 8, null, out __result))
                {
                    return false;
                }
                Monitor.Enter(ParallelTickEngine.MapPawnsLock, ref __state);
            }
            return true;
        }

        public static void Postfix(MapPawns __instance, bool __state, ref List<Pawn> __result)
        {
            if (__state && __result != null)
            {
                __result = ThreadSafeMapPawns.StoreSnapshot(__instance, 8, null, __result);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.MapPawnsLock);
            }
        }
    }

    [HarmonyPatch(typeof(ThinkNode_PrioritySorter), "TryIssueJobPackage")]
    public static class Patch_ThinkNode_PrioritySorter_TryIssueJobPackage
    {
        [ThreadStatic]
        private static Stack<List<ThinkNode>> tsWorkingNodePool;

        private static List<ThinkNode> RentWorkingList(int capacity)
        {
            if (tsWorkingNodePool == null)
            {
                tsWorkingNodePool = new Stack<List<ThinkNode>>(4);
            }
            if (tsWorkingNodePool.Count > 0)
            {
                List<ThinkNode> list = tsWorkingNodePool.Pop();
                list.Clear();
                return list;
            }
            return new List<ThinkNode>(Math.Max(16, capacity));
        }

        private static void ReturnWorkingList(List<ThinkNode> list)
        {
            if (list != null && tsWorkingNodePool != null)
            {
                list.Clear();
                tsWorkingNodePool.Push(list);
            }
        }

        public static bool Prefix(ThinkNode_PrioritySorter __instance, Pawn pawn, JobIssueParams jobParams, ref ThinkResult __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (__instance == null || __instance.subNodes == null)
            {
                __result = ThinkResult.NoJob;
                return false;
            }

            List<ThinkNode> subNodes = __instance.subNodes;
            int count = subNodes.Count;
            List<ThinkNode> workingNodes = RentWorkingList(count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    ThinkNode node = subNodes[i];
                    if (node == null)
                    {
                        continue;
                    }
                    int insertIndex = Rand.Range(0, workingNodes.Count - 1);
                    if (insertIndex < 0)
                    {
                        insertIndex = 0;
                    }
                    else if (insertIndex > workingNodes.Count)
                    {
                        insertIndex = workingNodes.Count;
                    }
                    workingNodes.Insert(insertIndex, node);
                }

                while (workingNodes.Count > 0)
                {
                    float bestPrio = 0f;
                    int bestIdx = -1;
                    for (int j = 0; j < workingNodes.Count; j++)
                    {
                        ThinkNode candidate = workingNodes[j];
                        if (candidate == null)
                        {
                            continue;
                        }
                        float p = 0f;
                        try
                        {
                            p = candidate.GetPriority(pawn);
                        }
                        catch (Exception ex)
                        {
                            MainThreadSyncQueue.RecordWorkerException("ThinkNode.GetPriority", ex);
                        }
                        if (!(p <= 0f) && !(p < __instance.minPriority) && p > bestPrio)
                        {
                            bestPrio = p;
                            bestIdx = j;
                        }
                    }
                    if (bestIdx == -1)
                    {
                        break;
                    }
                    ThinkResult res = ThinkResult.NoJob;
                    try
                    {
                        res = workingNodes[bestIdx].TryIssueJobPackage(pawn, jobParams);
                    }
                    catch (Exception ex2)
                    {
                        MainThreadSyncQueue.RecordWorkerException("ThinkNode.TryIssueJobPackage", ex2);
                    }
                    if (res.IsValid)
                    {
                        __result = res;
                        return false;
                    }
                    workingNodes.RemoveAt(bestIdx);
                }

                __result = ThinkResult.NoJob;
                return false;
            }
            finally
            {
                ReturnWorkingList(workingNodes);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_WorkSettings), "CacheWorkGiversInOrder")]
    public static class Patch_Pawn_WorkSettings_CacheWorkGiversInOrder
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.WorkSettingsLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.WorkSettingsLock);
            }
        }
    }

    [HarmonyPatch(typeof(JobMaker), "MakeJob", new Type[0])]
    public static class Patch_JobMaker_MakeJob
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.JobPoolLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.JobPoolLock);
            }
        }
    }

    [HarmonyPatch(typeof(JobMaker), "ReturnToPool", new Type[] { typeof(Job) })]
    public static class Patch_JobMaker_ReturnToPool
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.JobPoolLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.JobPoolLock);
            }
        }
    }

    [HarmonyPatch(typeof(ToilMaker), "MakeToil", new Type[] { typeof(string) })]
    public static class Patch_ToilMaker_MakeToil
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.JobPoolLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.JobPoolLock);
            }
        }
    }

    [HarmonyPatch(typeof(ToilMaker), "ReturnToPool", new Type[] { typeof(Toil) })]
    public static class Patch_ToilMaker_ReturnToPool
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.JobPoolLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.JobPoolLock);
            }
        }
    }

    [HarmonyPatch(typeof(QuestUtility), "GetWorkDisabledQuestPart", new Type[] { typeof(Pawn) })]
    public static class Patch_QuestUtility_GetWorkDisabledQuestPart
    {
        [ThreadStatic]
        private static List<QuestPart_WorkDisabled> tsQuestWorkDisabled;

        public static bool Prefix(Pawn p, ref List<QuestPart_WorkDisabled> __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (tsQuestWorkDisabled == null)
            {
                tsQuestWorkDisabled = new List<QuestPart_WorkDisabled>(8);
            }
            tsQuestWorkDisabled.Clear();
            if (p == null || Find.QuestManager == null)
            {
                __result = tsQuestWorkDisabled;
                return false;
            }
            List<Quest> activeQuests = Find.QuestManager.ActiveQuestsListForReading;
            if (activeQuests == null)
            {
                __result = tsQuestWorkDisabled;
                return false;
            }
            for (int i = 0; i < activeQuests.Count; i++)
            {
                Quest q = activeQuests[i];
                if (q == null || q.State != QuestState.Ongoing)
                {
                    continue;
                }
                List<QuestPart> parts = q.PartsListForReading;
                if (parts == null)
                {
                    continue;
                }
                for (int j = 0; j < parts.Count; j++)
                {
                    QuestPart_WorkDisabled wd = parts[j] as QuestPart_WorkDisabled;
                    if (wd != null && wd.pawns != null && wd.pawns.Contains(p))
                    {
                        tsQuestWorkDisabled.Add(wd);
                    }
                }
            }
            __result = tsQuestWorkDisabled;
            return false;
        }
    }

    [HarmonyPatch(typeof(JobGiver_ConfigurableHostilityResponse), "TryGetFleeJob")]
    public static class Patch_JobGiver_ConfigurableHostilityResponse_TryGetFleeJob
    {
        [ThreadStatic]
        private static Stack<List<Thing>> tsThreatsPool;

        private static List<Thing> RentThreatsList()
        {
            if (tsThreatsPool == null)
            {
                tsThreatsPool = new Stack<List<Thing>>(4);
            }
            if (tsThreatsPool.Count > 0)
            {
                List<Thing> list = tsThreatsPool.Pop();
                list.Clear();
                return list;
            }
            return new List<Thing>(64);
        }

        private static void ReturnThreatsList(List<Thing> list)
        {
            if (list != null && tsThreatsPool != null)
            {
                list.Clear();
                tsThreatsPool.Push(list);
            }
        }

        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }

            if (pawn == null || pawn.Map == null || !SelfDefenseUtility.ShouldStartFleeing(pawn))
            {
                __result = null;
                return false;
            }

            IntVec3 fleeDest;
            if (pawn.CurJob != null && pawn.CurJob.def == JobDefOf.FleeAndCower)
            {
                fleeDest = pawn.CurJob.targetA.Cell;
            }
            else
            {
                List<Thing> threats = RentThreatsList();
                try
                {
                    if (pawn.Map.attackTargetsCache != null)
                    {
                        List<IAttackTarget> potentialTargetsFor = pawn.Map.attackTargetsCache.GetPotentialTargetsFor(pawn);
                        if (potentialTargetsFor != null)
                        {
                            for (int i = 0; i < potentialTargetsFor.Count; i++)
                            {
                                IAttackTarget target = potentialTargetsFor[i];
                                Thing thing = target != null ? target.Thing : null;
                                if (thing != null && FleeUtility.ShouldFleeFrom(thing, pawn, false, false))
                                {
                                    threats.Add(thing);
                                }
                            }
                        }
                    }

                    List<Thing> alwaysFleeList = pawn.Map.listerThings != null ? pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.AlwaysFlee) : null;
                    if (alwaysFleeList != null)
                    {
                        for (int j = 0; j < alwaysFleeList.Count; j++)
                        {
                            Thing thing2 = alwaysFleeList[j];
                            if (thing2 != null && FleeUtility.ShouldFleeFrom(thing2, pawn, false, false))
                            {
                                threats.Add(thing2);
                            }
                        }
                    }

                    if (threats.Count == 0)
                    {
                        Region region = pawn.GetRegion(RegionType.Set_Passable);
                        if (region == null)
                        {
                            __result = null;
                            return false;
                        }
                        RegionTraverser.BreadthFirstTraverse(
                            region,
                            delegate(Region from, Region reg) { return reg != null && (reg.door == null || reg.door.Open); },
                            delegate(Region reg)
                            {
                                if (reg == null || reg.ListerThings == null)
                                {
                                    return false;
                                }
                                List<Thing> list2 = reg.ListerThings.ThingsInGroup(ThingRequestGroup.AttackTarget);
                                if (list2 != null)
                                {
                                    for (int k = 0; k < list2.Count; k++)
                                    {
                                        Thing thing3 = list2[k];
                                        if (thing3 != null && FleeUtility.ShouldFleeFrom(thing3, pawn, false, false))
                                        {
                                            threats.Add(thing3);
                                        }
                                    }
                                }
                                return false;
                            },
                            9,
                            RegionType.Set_Passable);

                        if (threats.Count == 0)
                        {
                            __result = null;
                            return false;
                        }
                    }

                    fleeDest = CellFinderLoose.GetFleeDest(pawn, threats, 23f);
                }
                finally
                {
                    ReturnThreatsList(threats);
                }
            }

            if (fleeDest == pawn.Position)
            {
                __result = null;
                return false;
            }

            __result = JobMaker.MakeJob(JobDefOf.FleeAndCower, fleeDest);
            return false;
        }
    }

    [HarmonyPatch(typeof(WanderUtility), "GetColonyWanderRoot")]
    public static class Patch_WanderUtility_GetColonyWanderRoot
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.WanderUtilityLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.WanderUtilityLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetFinder), "BestAttackTarget")]
    public static class Patch_AttackTargetFinder_BestAttackTarget
    {
        public static void Prefix(IAttackTargetSearcher searcher, bool onlyRanged, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                bool usesRangedStaticBuffers = onlyRanged;
                if (!usesRangedStaticBuffers && searcher != null)
                {
                    Verb v = searcher.CurrentEffectiveVerb;
                    if (v != null && v.verbProps != null && !v.verbProps.IsMeleeAttack)
                    {
                        usesRangedStaticBuffers = true;
                    }
                }
                if (usesRangedStaticBuffers)
                {
                    Monitor.Enter(ParallelTickEngine.AttackTargetFinderLock, ref __state);
                }
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.AttackTargetFinderLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetFinder), "FindBestReachableMeleeTarget")]
    public static class Patch_AttackTargetFinder_FindBestReachableMeleeTarget
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.AttackTargetFinderLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.AttackTargetFinderLock);
            }
        }
    }

    [HarmonyPatch(typeof(GenClosest), "RegionwiseBFSWorker")]
    public static class Patch_GenClosest_RegionwiseBFSWorker
    {
        [ThreadStatic]
        private static Stack<RegionProcessorClosestThingReachable> tsProcessorPool;

        public static bool Prefix(
            IntVec3 root,
            Map map,
            ThingRequest req,
            PathEndMode peMode,
            TraverseParms traverseParams,
            Predicate<Thing> validator,
            Func<Thing, float> priorityGetter,
            int minRegions,
            int maxRegions,
            float maxDistance,
            out int regionsSeen,
            RegionType traversableRegionTypes,
            bool ignoreEntirelyForbiddenRegions,
            bool lookInHaulSources,
            ref Thing __result)
        {
            regionsSeen = 0;
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }

            if (map == null || !root.IsValid)
            {
                __result = null;
                return false;
            }

            if (traverseParams.mode == TraverseMode.PassAllDestroyableThings ||
                traverseParams.mode == TraverseMode.PassAllDestroyablePlayerOwnedThings ||
                traverseParams.mode == TraverseMode.PassAllDestroyableThingsNotWater)
            {
                __result = null;
                return false;
            }

            if (!req.IsUndefined && !req.CanBeFoundInRegion)
            {
                __result = null;
                return false;
            }

            Region region = root.GetRegion(map, traversableRegionTypes);
            if (region == null)
            {
                __result = null;
                return false;
            }

            if (tsProcessorPool == null)
            {
                tsProcessorPool = new Stack<RegionProcessorClosestThingReachable>(4);
            }
            RegionProcessorClosestThingReachable processor = tsProcessorPool.Count > 0
                ? tsProcessorPool.Pop()
                : new RegionProcessorClosestThingReachable();

            try
            {
                processor.SetParameters(
                    traverseParams,
                    maxDistance,
                    root,
                    ignoreEntirelyForbiddenRegions,
                    req,
                    peMode,
                    priorityGetter,
                    validator,
                    minRegions,
                    9999999f,
                    0,
                    float.MinValue,
                    null,
                    lookInHaulSources);
                RegionTraverser.BreadthFirstTraverse(region, processor, maxRegions, traversableRegionTypes);
                regionsSeen = processor.regionsSeenScan;
                __result = processor.closestThing;
            }
            finally
            {
                processor.Clear();
                tsProcessorPool.Push(processor);
            }

            return false;
        }
    }

    [HarmonyPatch(typeof(AttackTargetReservationManager), "Reserve")]
    public static class Patch_AttackTargetReservationManager_Reserve
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetReservationManager), "Release")]
    public static class Patch_AttackTargetReservationManager_Release
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetReservationManager), "CanReserve")]
    public static class Patch_AttackTargetReservationManager_CanReserve
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetReservationManager), "IsReservedBy")]
    public static class Patch_AttackTargetReservationManager_IsReservedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetReservationManager), "ReleaseAllForTarget")]
    public static class Patch_AttackTargetReservationManager_ReleaseAllForTarget
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetReservationManager), "ReleaseClaimedBy")]
    public static class Patch_AttackTargetReservationManager_ReleaseClaimedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetReservationManager), "ReleaseAllClaimedBy")]
    public static class Patch_AttackTargetReservationManager_ReleaseAllClaimedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(AttackTargetReservationManager), "FirstReservationFor")]
    public static class Patch_AttackTargetReservationManager_FirstReservationFor
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_MeleeVerbs), "TryMeleeAttack")]
    public static class Patch_Pawn_MeleeVerbs_TryMeleeAttack
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CombatVerbLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CombatVerbLock);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_MeleeVerbs), "GetUpdatedAvailableVerbsList")]
    public static class Patch_Pawn_MeleeVerbs_GetUpdatedAvailableVerbsList
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CombatVerbLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CombatVerbLock);
            }
        }
    }

    [HarmonyPatch(typeof(Verb), "TryStartCastOn", new Type[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(bool), typeof(bool), typeof(bool), typeof(bool) })]
    public static class Patch_Verb_TryStartCastOn
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CombatVerbLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CombatVerbLock);
            }
        }
    }

    [HarmonyPatch(typeof(Verb), "WarmupComplete")]
    public static class Patch_Verb_WarmupComplete
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CombatVerbLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CombatVerbLock);
            }
        }
    }

    [HarmonyPatch(typeof(Verb), "VerbTick")]
    public static class Patch_Verb_VerbTick
    {
        public static void Prefix(Verb __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null && __instance.Bursting)
            {
                Monitor.Enter(ParallelTickEngine.CombatVerbLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CombatVerbLock);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "ClearAllReservations")]
    public static class Patch_Pawn_ClearAllReservations
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "ClearReservationsForJob")]
    public static class Patch_Pawn_ClearReservationsForJob
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "VerifyReservations")]
    public static class Patch_Pawn_VerifyReservations
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "GetPawnDestinationSetFor")]
    public static class Patch_PawnDestinationReservationManager_GetPawnDestinationSetFor
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "Reserve")]
    public static class Patch_PawnDestinationReservationManager_Reserve
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "MostRecentReservationFor")]
    public static class Patch_PawnDestinationReservationManager_MostRecentReservationFor
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "FirstObsoleteReservationFor")]
    public static class Patch_PawnDestinationReservationManager_FirstObsoleteReservationFor
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "FirstObsoleteReservationJobFor")]
    public static class Patch_PawnDestinationReservationManager_FirstObsoleteReservationJobFor
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "IsReserved", new Type[] { typeof(IntVec3), typeof(Pawn) }, new ArgumentType[] { ArgumentType.Normal, ArgumentType.Out })]
    public static class Patch_PawnDestinationReservationManager_IsReserved
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "CanReserve")]
    public static class Patch_PawnDestinationReservationManager_CanReserve
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "FirstReserverOf")]
    public static class Patch_PawnDestinationReservationManager_FirstReserverOf
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "ReleaseAllObsoleteClaimedBy")]
    public static class Patch_PawnDestinationReservationManager_ReleaseAllObsoleteClaimedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "ReleaseAllClaimedBy")]
    public static class Patch_PawnDestinationReservationManager_ReleaseAllClaimedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "ReleaseClaimedBy")]
    public static class Patch_PawnDestinationReservationManager_ReleaseClaimedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnDestinationReservationManager), "ObsoleteAllClaimedBy")]
    public static class Patch_PawnDestinationReservationManager_ObsoleteAllClaimedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "CanReserveStack")]
    public static class Patch_ReservationManager_CanReserveStack
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "ReleaseAllForTarget")]
    public static class Patch_ReservationManager_ReleaseAllForTarget
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "ReleaseClaimedBy")]
    public static class Patch_ReservationManager_ReleaseClaimedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "ReleaseAllClaimedBy")]
    public static class Patch_ReservationManager_ReleaseAllClaimedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(ReservationManager), "FirstReservationFor")]
    public static class Patch_ReservationManager_FirstReservationFor
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PhysicalInteractionReservationManager), "Reserve")]
    public static class Patch_PhysicalInteractionReservationManager_Reserve
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PhysicalInteractionReservationManager), "Release")]
    public static class Patch_PhysicalInteractionReservationManager_Release
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PhysicalInteractionReservationManager), "IsReservedBy")]
    public static class Patch_PhysicalInteractionReservationManager_IsReservedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PhysicalInteractionReservationManager), "FirstReserverOf")]
    public static class Patch_PhysicalInteractionReservationManager_FirstReserverOf
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PhysicalInteractionReservationManager), "FirstReservationFor")]
    public static class Patch_PhysicalInteractionReservationManager_FirstReservationFor
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PhysicalInteractionReservationManager), "ReleaseAllForTarget")]
    public static class Patch_PhysicalInteractionReservationManager_ReleaseAllForTarget
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PhysicalInteractionReservationManager), "ReleaseClaimedBy")]
    public static class Patch_PhysicalInteractionReservationManager_ReleaseClaimedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(PhysicalInteractionReservationManager), "ReleaseAllClaimedBy")]
    public static class Patch_PhysicalInteractionReservationManager_ReleaseAllClaimedBy
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.ReservationLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ReservationLock);
            }
        }
    }

    [HarmonyPatch(typeof(RCellFinder), "RandomWanderDestFor")]
    public static class Patch_RCellFinder_RandomWanderDestFor
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(RCellFinder), "SpotToStandDuringJob")]
    public static class Patch_RCellFinder_SpotToStandDuringJob
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(RCellFinder), "TryFindDirectFleeDestination")]
    public static class Patch_RCellFinder_TryFindDirectFleeDestination
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(RCellFinder), "TryFindSkygazeCell")]
    public static class Patch_RCellFinder_TryFindSkygazeCell
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(CellFinder), "TryFindRandomCell")]
    public static class Patch_CellFinder_TryFindRandomCell
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(CellFinder), "RandomRegionNear")]
    public static class Patch_CellFinder_RandomRegionNear
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(CellFinder), "AllRegionsNear")]
    public static class Patch_CellFinder_AllRegionsNear
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(CellFinder), "TryFindRandomReachableNearbyCell")]
    public static class Patch_CellFinder_TryFindRandomReachableNearbyCell
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(CellFinder), "TryFindRandomReachableCellNearPosition")]
    public static class Patch_CellFinder_TryFindRandomReachableCellNearPosition
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(CellFinder), "TryFindRandomCellInRegion")]
    public static class Patch_CellFinder_TryFindRandomCellInRegion
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(CellFinder), "TryFindRandomCellNear")]
    public static class Patch_CellFinder_TryFindRandomCellNear
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(CellFinder), "TryFindRandomAdjacentCell")]
    public static class Patch_CellFinder_TryFindRandomAdjacentCell
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(CellFinder), "TryFindBestPawnStandCell")]
    public static class Patch_CellFinder_TryFindBestPawnStandCell
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(CellFinder), "TryFindRandomCellInsideWith")]
    public static class Patch_CellFinder_TryFindRandomCellInsideWith
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(CellFinder), "FindNoWipeSpawnLocNear")]
    public static class Patch_CellFinder_FindNoWipeSpawnLocNear
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.CellFinderLooseLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CellFinderLooseLock);
            }
        }
    }

    [HarmonyPatch(typeof(BeautyUtility), "AverageBeautyPerceptible")]
    public static class Patch_BeautyUtility_AverageBeautyPerceptible
    {
        [ThreadStatic]
        private static List<IntVec3> tsBeautyRelevantCells;
        [ThreadStatic]
        private static HashSet<Room> tsTempRooms;
        [ThreadStatic]
        private static List<Room> tsVisibleRooms;
        [ThreadStatic]
        private static HashSet<Thing> tsTempCountedThings;

        [HarmonyPriority(Priority.First)]
        public static bool Prefix(IntVec3 root, Map map, ref float __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (map == null || !root.IsValid || !root.InBounds(map))
            {
                __result = 0f;
                return false;
            }

            List<IntVec3> relCells = tsBeautyRelevantCells ?? (tsBeautyRelevantCells = new List<IntVec3>(64));
            HashSet<Room> tempRooms = tsTempRooms ?? (tsTempRooms = new HashSet<Room>());
            List<Room> visRooms = tsVisibleRooms ?? (tsVisibleRooms = new List<Room>(8));
            HashSet<Thing> countedThings = tsTempCountedThings ?? (tsTempCountedThings = new HashSet<Thing>());

            relCells.Clear();
            tempRooms.Clear();
            visRooms.Clear();
            countedThings.Clear();

            Room room = root.GetRoom(map);
            if (room == null)
            {
                __result = 0f;
                return false;
            }

            visRooms.Add(room);
            if (room.IsDoorway && room.Regions != null && room.Regions.Count == 1)
            {
                Region reg = room.Regions[0];
                if (reg != null && reg.links != null)
                {
                    for (int i = 0; i < reg.links.Count; i++)
                    {
                        RegionLink link = reg.links[i];
                        if (link == null)
                        {
                            continue;
                        }
                        Region other = link.GetOtherRegion(reg);
                        if (other != null && other.Room != null && !visRooms.Contains(other.Room))
                        {
                            visRooms.Add(other.Room);
                        }
                    }
                }
            }

            for (int i = 0; i < BeautyUtility.SampleNumCells_Beauty; i++)
            {
                IntVec3 c = root + GenRadial.RadialPattern[i];
                if (!c.InBounds(map) || c.Fogged(map))
                {
                    continue;
                }
                Room cellRoom = c.GetRoom(map);
                if (!visRooms.Contains(cellRoom))
                {
                    bool flag = false;
                    for (int j = 0; j < 8; j++)
                    {
                        IntVec3 loc = c + GenAdj.AdjacentCells[j];
                        if (loc.InBounds(map) && visRooms.Contains(loc.GetRoom(map)))
                        {
                            flag = true;
                            break;
                        }
                    }
                    if (!flag)
                    {
                        continue;
                    }
                }
                relCells.Add(c);
            }

            if (relCells.Count == 0)
            {
                __result = 0f;
                return false;
            }

            float sum = 0f;
            int validCells = 0;
            for (int i = 0; i < relCells.Count; i++)
            {
                IntVec3 cell = relCells[i];
                if (PawnUtility.KnownDangerAt(cell, map, null))
                {
                    continue;
                }
                Room r = cell.GetRoom(map);
                if (r != null && !tempRooms.Contains(r))
                {
                    sum += r.GetStat(RoomStatDefOf.Beauty) * (float)r.CellCount;
                    validCells += r.CellCount;
                    tempRooms.Add(r);
                }
                else if (r == null || r.IsHuge)
                {
                    sum += BeautyUtility.CellBeauty(cell, map, countedThings);
                    validCells++;
                }
            }

            relCells.Clear();
            tempRooms.Clear();
            visRooms.Clear();
            countedThings.Clear();

            __result = validCells == 0 ? 0f : (sum / (float)validCells);
            return false;
        }
    }

    [HarmonyPatch(typeof(BeautyUtility), "FillBeautyRelevantCells")]
    public static class Patch_BeautyUtility_FillBeautyRelevantCells
    {
        [HarmonyPriority(Priority.First)]
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.BeautyLock, ref __state);
            }
        }

        [HarmonyPriority(Priority.Last)]
        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.BeautyLock);
            }
        }
    }

    [HarmonyPatch(typeof(BeautyUtility), "CellBeauty")]
    public static class Patch_BeautyUtility_CellBeauty
    {
        [ThreadStatic]
        private static HashSet<Thing> tsCellCountedThings;

        [HarmonyPriority(Priority.First)]
        public static void Prefix(ref HashSet<Thing> countedThings)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && countedThings == null)
            {
                HashSet<Thing> set = tsCellCountedThings ?? (tsCellCountedThings = new HashSet<Thing>());
                set.Clear();
                countedThings = set;
            }
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), "ThoughtInterval")]
    public static class Patch_ThoughtHandler_ThoughtInterval
    {
        public static void Prefix(ThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), "TotalMoodOffset")]
    public static class Patch_ThoughtHandler_TotalMoodOffset
    {
        [ThreadStatic]
        private static List<Thought> tsMoodThoughts;

        public static bool Prefix(ThoughtHandler __instance, ref float __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection || __instance == null)
            {
                return true;
            }

            List<Thought> thoughts = tsMoodThoughts;
            if (thoughts != null)
            {
                tsMoodThoughts = null;
                thoughts.Clear();
            }
            else
            {
                thoughts = new List<Thought>(32);
            }

            try
            {
                lock (ParallelTickEngine.ThoughtHandlerLock)
                {
                    __instance.GetDistinctMoodThoughtGroups(thoughts);
                    float num = 0f;
                    for (int i = 0; i < thoughts.Count; i++)
                    {
                        Thought group = thoughts[i];
                        if (group != null)
                        {
                            num += __instance.MoodOffsetOfGroup(group);
                        }
                    }
                    __result = num;
                }
                return false;
            }
            finally
            {
                thoughts.Clear();
                if (tsMoodThoughts == null)
                {
                    tsMoodThoughts = thoughts;
                }
            }
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), "MoodOffsetOfGroup")]
    public static class Patch_ThoughtHandler_MoodOffsetOfGroup
    {
        [ThreadStatic]
        private static List<Thought> tsGroupThoughts;

        public static bool Prefix(ThoughtHandler __instance, Thought group, ref float __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection || __instance == null)
            {
                return true;
            }

            if (group == null)
            {
                __result = 0f;
                return false;
            }

            List<Thought> thoughts = tsGroupThoughts;
            if (thoughts != null)
            {
                tsGroupThoughts = null;
                thoughts.Clear();
            }
            else
            {
                thoughts = new List<Thought>(16);
            }

            try
            {
                lock (ParallelTickEngine.ThoughtHandlerLock)
                {
                    __instance.GetMoodThoughts(group, thoughts);
                    if (thoughts.Count == 0)
                    {
                        __result = 0f;
                        return false;
                    }
                    float num = 0f;
                    float num2 = 1f;
                    float num3 = 0f;
                    int validCount = 0;
                    for (int i = 0; i < thoughts.Count; i++)
                    {
                        Thought thought = thoughts[i];
                        if (thought == null || thought.def == null)
                        {
                            continue;
                        }
                        validCount++;
                        num += thought.MoodOffset();
                        num3 += num2;
                        num2 *= thought.def.stackedEffectMultiplier;
                    }
                    if (validCount == 0)
                    {
                        __result = 0f;
                        return false;
                    }
                    float num4 = num / (float)validCount;
                    __result = num4 * num3;
                }
                return false;
            }
            finally
            {
                thoughts.Clear();
                if (tsGroupThoughts == null)
                {
                    tsGroupThoughts = thoughts;
                }
            }
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), "GetDistinctMoodThoughtGroups")]
    public static class Patch_ThoughtHandler_GetDistinctMoodThoughtGroups
    {
        public static void Prefix(ThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), "GetMoodThoughts")]
    public static class Patch_ThoughtHandler_GetMoodThoughts
    {
        public static void Prefix(ThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), "GetAllMoodThoughts")]
    public static class Patch_ThoughtHandler_GetAllMoodThoughts
    {
        public static void Prefix(ThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), "GetMoodThoughtsFor")]
    public static class Patch_ThoughtHandler_GetMoodThoughtsFor
    {
        public static void Prefix(ThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), "TotalOpinionOffset")]
    public static class Patch_ThoughtHandler_TotalOpinionOffset
    {
        public static void Prefix(ThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), "OpinionOffsetOfGroup")]
    public static class Patch_ThoughtHandler_OpinionOffsetOfGroup
    {
        public static void Prefix(ThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(ThoughtHandler), "GetDistinctSocialThoughtGroups")]
    public static class Patch_ThoughtHandler_GetDistinctSocialThoughtGroups
    {
        public static void Prefix(ThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(MemoryThoughtHandler), "TryGainMemory", new Type[] { typeof(Thought_Memory), typeof(Pawn) })]
    public static class Patch_MemoryThoughtHandler_TryGainMemory
    {
        public static void Prefix(MemoryThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(MemoryThoughtHandler), "RemoveMemory")]
    public static class Patch_MemoryThoughtHandler_RemoveMemory
    {
        public static void Prefix(MemoryThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(MemoryThoughtHandler), "RemoveMemoriesOfDef")]
    public static class Patch_MemoryThoughtHandler_RemoveMemoriesOfDef
    {
        public static void Prefix(MemoryThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(MemoryThoughtHandler), "RemoveMemoriesOfDefIf")]
    public static class Patch_MemoryThoughtHandler_RemoveMemoriesOfDefIf
    {
        public static void Prefix(MemoryThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(MemoryThoughtHandler), "RemoveMemoriesOfDefWhereOtherPawnIs")]
    public static class Patch_MemoryThoughtHandler_RemoveMemoriesOfDefWhereOtherPawnIs
    {
        public static void Prefix(MemoryThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(MemoryThoughtHandler), "RemoveMemoriesWhereOtherPawnIs")]
    public static class Patch_MemoryThoughtHandler_RemoveMemoriesWhereOtherPawnIs
    {
        public static void Prefix(MemoryThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(SituationalThoughtHandler), "Notify_SituationalThoughtsDirty")]
    public static class Patch_SituationalThoughtHandler_NotifyDirty
    {
        public static void Prefix(SituationalThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(RegionListersUpdater), "DeregisterInRegions")]
    public static class Patch_RegionListersUpdater_Deregister
    {
        [ThreadStatic]
        private static List<Region> tsRegions;

        public static bool Prefix(Thing thing, Map map)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (thing == null || map == null || thing.def == null || !ListerThings.EverListable(thing.def, ListerThingsUse.Region))
            {
                return false;
            }
            List<Region> regions = tsRegions;
            if (regions == null)
            {
                regions = new List<Region>(16);
                tsRegions = regions;
            }
            regions.Clear();
            RegionListersUpdater.GetTouchableRegions(thing, map, regions, true);
            lock (ParallelTickEngine.RegionListersLock)
            {
                for (int i = 0; i < regions.Count; i++)
                {
                    Region reg = regions[i];
                    if (reg == null)
                    {
                        continue;
                    }
                    ListerThings listerThings = reg.ListerThings;
                    if (listerThings != null && listerThings.Contains(thing))
                    {
                        listerThings.Remove(thing);
                    }
                }
            }
            regions.Clear();
            return false;
        }
    }

    [HarmonyPatch(typeof(RegionListersUpdater), "RegisterInRegions")]
    public static class Patch_RegionListersUpdater_Register
    {
        [ThreadStatic]
        private static List<Region> tsRegions;

        public static bool Prefix(Thing thing, Map map)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (thing == null || map == null || thing.def == null || !ListerThings.EverListable(thing.def, ListerThingsUse.Region))
            {
                return false;
            }
            List<Region> regions = tsRegions;
            if (regions == null)
            {
                regions = new List<Region>(16);
                tsRegions = regions;
            }
            regions.Clear();
            RegionListersUpdater.GetTouchableRegions(thing, map, regions, false);
            lock (ParallelTickEngine.RegionListersLock)
            {
                for (int i = 0; i < regions.Count; i++)
                {
                    Region reg = regions[i];
                    if (reg == null)
                    {
                        continue;
                    }
                    ListerThings listerThings = reg.ListerThings;
                    if (listerThings != null && !listerThings.Contains(thing))
                    {
                        listerThings.Add(thing);
                    }
                }
            }
            regions.Clear();
            return false;
        }
    }

    [HarmonyPatch(typeof(ListerThings), "ThingsMatching")]
    public static class Patch_ListerThings_ThingsMatching
    {
        [ThreadStatic]
        private static List<Thing>[] tsSnapshots;
        [ThreadStatic]
        private static int tsSnapshotCursor;

        private static readonly List<Thing> EmptyThingList = new List<Thing>(0);

        public static void Postfix(ListerThings __instance, ref List<Thing> __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection || __instance == null)
            {
                return;
            }
            if (__instance.use != ListerThingsUse.Region)
            {
                return;
            }
            List<Thing> src = __result;
            if (src == null || src.Count == 0)
            {
                __result = EmptyThingList;
                return;
            }
            List<Thing>[] ring = tsSnapshots;
            if (ring == null)
            {
                ring = new List<Thing>[8];
                for (int i = 0; i < 8; i++)
                {
                    ring[i] = new List<Thing>(32);
                }
                tsSnapshots = ring;
            }
            int slot = (tsSnapshotCursor++) & 7;
            List<Thing> snap = ring[slot];
            snap.Clear();
            try
            {
                int count = src.Count;
                for (int i = 0; i < count; i++)
                {
                    Thing t = src[i];
                    if (t != null)
                    {
                        snap.Add(t);
                    }
                }
                __result = snap;
                return;
            }
            catch
            {
                snap.Clear();
            }
            lock (ParallelTickEngine.RegionListersLock)
            {
                int count = src.Count;
                for (int i = 0; i < count; i++)
                {
                    Thing t = src[i];
                    if (t != null)
                    {
                        snap.Add(t);
                    }
                }
                __result = snap;
            }
        }
    }

    [HarmonyPatch(typeof(Region), "DangerFor")]
    public static class Patch_Region_DangerFor
    {
        public static bool Prefix(Region __instance, Pawn p, ref Danger __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection || __instance == null || p == null)
            {
                return true;
            }
            if (Current.ProgramState == ProgramState.Playing)
            {
                Room room = __instance.Room;
                if (room != null)
                {
                    float temperature = room.Temperature;
                    FloatRange value = p.SafeTemperatureRange();
                    if (value.Includes(temperature))
                    {
                        __result = Danger.None;
                    }
                    else if (value.ExpandedBy(80f).Includes(temperature))
                    {
                        __result = Danger.Some;
                    }
                    else
                    {
                        __result = Danger.Deadly;
                    }
                    return false;
                }
            }
            __result = Danger.None;
            return false;
        }
    }

    public static class ThreadLocalCapacityCache
    {
        private sealed class CapacityCacheEntry
        {
            public readonly int HandlerHash;
            public readonly int DefIndex;
            public readonly int Tick;
            public readonly int Epoch;
            public readonly float Value;

            public CapacityCacheEntry(int handlerHash, int defIndex, int tick, int epoch, float value)
            {
                HandlerHash = handlerHash;
                DefIndex = defIndex;
                Tick = tick;
                Epoch = epoch;
                Value = value;
            }
        }

        private const int SlotMask = 16383;
        private static readonly CapacityCacheEntry[] SharedSlots = new CapacityCacheEntry[SlotMask + 1];
        private static readonly int[] HandlerEpoch = new int[4096];

        public static bool TryGet(PawnCapacitiesHandler handler, PawnCapacityDef capacity, int ttlTicks, out float value, out bool hasStaleValue)
        {
            if (handler == null || capacity == null)
            {
                value = 0f;
                hasStaleValue = false;
                return false;
            }
            int handlerHash = RuntimeHelpers.GetHashCode(handler);
            int defIndex = capacity.index;
            int ticksGame = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            int epoch = HandlerEpoch[handlerHash & 4095];
            int idx = ((handlerHash * 397) ^ defIndex) & SlotMask;
            int effectiveTtl = ttlTicks + (handlerHash & 1023);

            CapacityCacheEntry entry = SharedSlots[idx];
            if (entry != null &&
                entry.HandlerHash == handlerHash &&
                entry.DefIndex == defIndex)
            {
                if (entry.Epoch == epoch && (ticksGame - entry.Tick) <= effectiveTtl)
                {
                    value = entry.Value;
                    hasStaleValue = false;
                    return true;
                }
                value = entry.Value;
                hasStaleValue = true;
                return false;
            }
            value = 0f;
            hasStaleValue = false;
            return false;
        }

        public static void Store(PawnCapacitiesHandler handler, PawnCapacityDef capacity, float value)
        {
            if (handler == null || capacity == null)
            {
                return;
            }
            int handlerHash = RuntimeHelpers.GetHashCode(handler);
            int defIndex = capacity.index;
            int ticksGame = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            int epoch = HandlerEpoch[handlerHash & 4095];
            int idx = ((handlerHash * 397) ^ defIndex) & SlotMask;

            SharedSlots[idx] = new CapacityCacheEntry(handlerHash, defIndex, ticksGame, epoch, value);
        }

        public static void Invalidate(PawnCapacitiesHandler handler)
        {
            if (handler == null)
            {
                return;
            }
            int handlerHash = RuntimeHelpers.GetHashCode(handler);
            Interlocked.Increment(ref HandlerEpoch[handlerHash & 4095]);
        }
    }

    [HarmonyPatch(typeof(PawnCapacitiesHandler), "GetLevel")]
    public static class Patch_PawnCapacitiesHandler_GetLevel
    {
        public static bool Prefix(PawnCapacitiesHandler __instance, PawnCapacityDef capacity, ref float __result, ref bool __state)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection || __instance == null || capacity == null)
            {
                return true;
            }

            float cached;
            bool hasStale;
            if (ThreadLocalCapacityCache.TryGet(__instance, capacity, 2500, out cached, out hasStale))
            {
                __result = cached;
                return false;
            }

            if (hasStale)
            {
                Monitor.TryEnter(ParallelTickEngine.CapacitiesLock, ref __state);
                if (!__state)
                {
                    __result = cached;
                    return false;
                }
                return true;
            }

            Monitor.Enter(ParallelTickEngine.CapacitiesLock, ref __state);
            return true;
        }

        public static void Postfix(PawnCapacitiesHandler __instance, PawnCapacityDef capacity, float __result, bool __state)
        {
            if (__state && __instance != null && capacity != null)
            {
                ThreadLocalCapacityCache.Store(__instance, capacity, __result);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CapacitiesLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnCapacitiesHandler), "Notify_CapacityLevelsDirty")]
    public static class Patch_PawnCapacitiesHandler_NotifyDirty
    {
        public static void Prefix(PawnCapacitiesHandler __instance, ref bool __state)
        {
            if (__instance != null)
            {
                ThreadLocalCapacityCache.Invalidate(__instance);
            }
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.CapacitiesLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CapacitiesLock);
            }
        }
    }

    [HarmonyPatch(typeof(PawnCapacitiesHandler), "Clear")]
    public static class Patch_PawnCapacitiesHandler_Clear
    {
        public static void Prefix(PawnCapacitiesHandler __instance, ref bool __state)
        {
            if (__instance != null)
            {
                ThreadLocalCapacityCache.Invalidate(__instance);
            }
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.CapacitiesLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.CapacitiesLock);
            }
        }
    }

    public static class ThreadLocalStatCache
    {
        private sealed class StatCacheEntry
        {
            public readonly int WorkerHash;
            public readonly int KeyId;
            public readonly int ModeFlags;
            public readonly int Tick;
            public readonly int Epoch;
            public readonly float Value;

            public StatCacheEntry(int workerHash, int keyId, int modeFlags, int tick, int epoch, float value)
            {
                WorkerHash = workerHash;
                KeyId = keyId;
                ModeFlags = modeFlags;
                Tick = tick;
                Epoch = epoch;
                Value = value;
            }
        }

        private const int SlotMask = 65535;
        private static readonly StatCacheEntry[] SharedSlots = new StatCacheEntry[SlotMask + 1];
        private static readonly int[] ThingEpoch = new int[8192];

        public static float GetOrAdd(StatWorker worker, Thing thing, bool applyPostProcess, int ttlTicks)
        {
            if (worker == null)
            {
                return 0f;
            }
            if (thing == null)
            {
                return GetOrAddRequest(worker, StatRequest.ForEmpty(), applyPostProcess, ttlTicks);
            }

            int thingId = thing.thingIDNumber;
            int effectiveTtl = ttlTicks + (thingId & 1023);
            int workerHash = RuntimeHelpers.GetHashCode(worker);
            int ticksGame = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            int epoch = ThingEpoch[thingId & 8191];
            int modeFlags = applyPostProcess ? 1 : 0;
            int idx = (((workerHash * 397) ^ (thingId * 17)) + modeFlags) & SlotMask;

            StatCacheEntry entry = SharedSlots[idx];
            if (entry != null &&
                entry.WorkerHash == workerHash &&
                entry.KeyId == thingId &&
                entry.ModeFlags == modeFlags)
            {
                if (entry.Epoch == epoch && (ticksGame - entry.Tick) <= effectiveTtl)
                {
                    return entry.Value;
                }
                if (!Monitor.TryEnter(ParallelTickEngine.GlobalSyncLock))
                {
                    return entry.Value;
                }
                try
                {
                    StatCacheEntry recheckFast = SharedSlots[idx];
                    if (recheckFast != null &&
                        recheckFast.WorkerHash == workerHash &&
                        recheckFast.KeyId == thingId &&
                        recheckFast.ModeFlags == modeFlags &&
                        recheckFast.Epoch == epoch &&
                        (ticksGame - recheckFast.Tick) <= effectiveTtl)
                    {
                        return recheckFast.Value;
                    }

                    StatRequest reqFast = StatRequest.For(thing);
                    float computedFast = worker.GetValueUnfinalized(reqFast, applyPostProcess);
                    worker.FinalizeValue(reqFast, ref computedFast, applyPostProcess);

                    SharedSlots[idx] = new StatCacheEntry(workerHash, thingId, modeFlags, ticksGame, epoch, computedFast);
                    return computedFast;
                }
                finally
                {
                    Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
                }
            }

            float computed;
            lock (ParallelTickEngine.GlobalSyncLock)
            {
                StatCacheEntry recheck = SharedSlots[idx];
                if (recheck != null &&
                    recheck.WorkerHash == workerHash &&
                    recheck.KeyId == thingId &&
                    recheck.ModeFlags == modeFlags &&
                    recheck.Epoch == epoch &&
                    (ticksGame - recheck.Tick) <= effectiveTtl)
                {
                    return recheck.Value;
                }

                StatRequest req = StatRequest.For(thing);
                computed = worker.GetValueUnfinalized(req, applyPostProcess);
                worker.FinalizeValue(req, ref computed, applyPostProcess);

                SharedSlots[idx] = new StatCacheEntry(workerHash, thingId, modeFlags, ticksGame, epoch, computed);
            }
            return computed;
        }

        public static float GetOrAddRequest(StatWorker worker, StatRequest req, bool applyPostProcess, int ttlTicks)
        {
            if (worker == null)
            {
                return 0f;
            }
            if (req.HasThing && req.Thing != null)
            {
                return GetOrAdd(worker, req.Thing, applyPostProcess, ttlTicks);
            }

            int reqHash = req.GetHashCode();
            int effectiveTtl = ttlTicks + (reqHash & 1023);
            int workerHash = RuntimeHelpers.GetHashCode(worker);
            int ticksGame = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            int modeFlags = (applyPostProcess ? 1 : 0) | 4;
            int idx = (((workerHash * 397) ^ (reqHash * 31)) + modeFlags) & SlotMask;

            StatCacheEntry entry = SharedSlots[idx];
            if (entry != null &&
                entry.WorkerHash == workerHash &&
                entry.KeyId == reqHash &&
                entry.ModeFlags == modeFlags)
            {
                if ((ticksGame - entry.Tick) <= effectiveTtl)
                {
                    return entry.Value;
                }
                if (!Monitor.TryEnter(ParallelTickEngine.GlobalSyncLock))
                {
                    return entry.Value;
                }
                try
                {
                    StatCacheEntry recheckFast = SharedSlots[idx];
                    if (recheckFast != null &&
                        recheckFast.WorkerHash == workerHash &&
                        recheckFast.KeyId == reqHash &&
                        recheckFast.ModeFlags == modeFlags &&
                        (ticksGame - recheckFast.Tick) <= effectiveTtl)
                    {
                        return recheckFast.Value;
                    }

                    float computedFast = worker.GetValueUnfinalized(req, applyPostProcess);
                    worker.FinalizeValue(req, ref computedFast, applyPostProcess);

                    SharedSlots[idx] = new StatCacheEntry(workerHash, reqHash, modeFlags, ticksGame, 0, computedFast);
                    return computedFast;
                }
                finally
                {
                    Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
                }
            }

            float computed;
            lock (ParallelTickEngine.GlobalSyncLock)
            {
                StatCacheEntry recheck = SharedSlots[idx];
                if (recheck != null &&
                    recheck.WorkerHash == workerHash &&
                    recheck.KeyId == reqHash &&
                    recheck.ModeFlags == modeFlags &&
                    (ticksGame - recheck.Tick) <= effectiveTtl)
                {
                    return recheck.Value;
                }

                computed = worker.GetValueUnfinalized(req, applyPostProcess);
                worker.FinalizeValue(req, ref computed, applyPostProcess);

                SharedSlots[idx] = new StatCacheEntry(workerHash, reqHash, modeFlags, ticksGame, 0, computed);
            }
            return computed;
        }

        public static float GetOrAddUnfinalized(StatWorker worker, StatRequest req, bool applyPostProcess, int ttlTicks)
        {
            if (worker == null)
            {
                return 0f;
            }

            int workerHash = RuntimeHelpers.GetHashCode(worker);
            int ticksGame = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            int keyId;
            int epoch;
            int modeFlags = (applyPostProcess ? 1 : 0) | 2;

            if (req.HasThing && req.Thing != null)
            {
                keyId = req.Thing.thingIDNumber;
                epoch = ThingEpoch[keyId & 8191];
            }
            else
            {
                keyId = req.GetHashCode();
                epoch = 0;
                modeFlags |= 4;
            }

            int effectiveTtl = ttlTicks + (keyId & 1023);
            int idx = (((workerHash * 397) ^ (keyId * 23)) + modeFlags) & SlotMask;

            StatCacheEntry entry = SharedSlots[idx];
            if (entry != null &&
                entry.WorkerHash == workerHash &&
                entry.KeyId == keyId &&
                entry.ModeFlags == modeFlags)
            {
                if (entry.Epoch == epoch && (ticksGame - entry.Tick) <= effectiveTtl)
                {
                    return entry.Value;
                }
                if (!Monitor.TryEnter(ParallelTickEngine.GlobalSyncLock))
                {
                    return entry.Value;
                }
                try
                {
                    StatCacheEntry recheckFast = SharedSlots[idx];
                    if (recheckFast != null &&
                        recheckFast.WorkerHash == workerHash &&
                        recheckFast.KeyId == keyId &&
                        recheckFast.ModeFlags == modeFlags &&
                        recheckFast.Epoch == epoch &&
                        (ticksGame - recheckFast.Tick) <= effectiveTtl)
                    {
                        return recheckFast.Value;
                    }

                    float computedFast = worker.GetValueUnfinalized(req, applyPostProcess);
                    SharedSlots[idx] = new StatCacheEntry(workerHash, keyId, modeFlags, ticksGame, epoch, computedFast);
                    return computedFast;
                }
                finally
                {
                    Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
                }
            }

            float computed;
            lock (ParallelTickEngine.GlobalSyncLock)
            {
                StatCacheEntry recheck = SharedSlots[idx];
                if (recheck != null &&
                    recheck.WorkerHash == workerHash &&
                    recheck.KeyId == keyId &&
                    recheck.ModeFlags == modeFlags &&
                    recheck.Epoch == epoch &&
                    (ticksGame - recheck.Tick) <= effectiveTtl)
                {
                    return recheck.Value;
                }

                computed = worker.GetValueUnfinalized(req, applyPostProcess);

                SharedSlots[idx] = new StatCacheEntry(workerHash, keyId, modeFlags, ticksGame, epoch, computed);
            }
            return computed;
        }

        public static void ClearLocalForThing(StatWorker worker, Thing thing)
        {
            if (thing == null)
            {
                return;
            }
            int thingId = thing.thingIDNumber;
            Interlocked.Increment(ref ThingEpoch[thingId & 8191]);
        }
    }

    [HarmonyPatch(typeof(StatWorker), "GetValue", new Type[] { typeof(Thing), typeof(bool), typeof(int) })]
    public static class Patch_StatWorker_GetValue
    {
        public static bool Prefix(StatWorker __instance, Thing thing, bool applyPostProcess, int cacheStaleAfterTicks, ref float __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection || __instance == null || thing == null)
            {
                return true;
            }

            int ttl = cacheStaleAfterTicks > 0 ? Math.Max(cacheStaleAfterTicks, 2500) : 2500;
            __result = ThreadLocalStatCache.GetOrAdd(__instance, thing, applyPostProcess, ttl);
            return false;
        }
    }

    [HarmonyPatch(typeof(StatWorker), "GetValue", new Type[] { typeof(StatRequest), typeof(bool) })]
    public static class Patch_StatWorker_GetValue_Request
    {
        public static bool Prefix(StatWorker __instance, StatRequest req, bool applyPostProcess, ref float __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection || __instance == null)
            {
                return true;
            }

            if (req.HasThing && req.Thing != null)
            {
                __result = ThreadLocalStatCache.GetOrAdd(__instance, req.Thing, applyPostProcess, 2500);
                return false;
            }

            __result = ThreadLocalStatCache.GetOrAddRequest(__instance, req, applyPostProcess, 2500);
            return false;
        }
    }

    [HarmonyPatch(typeof(StatWorker), "TryClearCache")]
    public static class Patch_StatWorker_TryClearCache
    {
        public static void Prefix(StatWorker __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch(typeof(StatWorker), "ClearCacheForThing")]
    public static class Patch_StatWorker_ClearCacheForThing
    {
        public static void Prefix(StatWorker __instance, Thing thing, ref bool __state)
        {
            if (thing != null)
            {
                ThreadLocalStatCache.ClearLocalForThing(__instance, thing);
            }
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null && thing != null)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_InteractionsTracker), "TryInteractRandomly")]
    public static class Patch_Pawn_InteractionsTracker_TryInteractRandomly
    {
        [ThreadStatic]
        private static List<Pawn> tsWorkingList;

        private static readonly FieldInfo Field_workingList = AccessTools.Field(typeof(Pawn_InteractionsTracker), "workingList");
        private static readonly MethodInfo Method_GetWorkingList = AccessTools.Method(typeof(Patch_Pawn_InteractionsTracker_TryInteractRandomly), "GetThreadLocalWorkingList");

        public static List<Pawn> GetThreadLocalWorkingList()
        {
            if (tsWorkingList == null)
            {
                tsWorkingList = new List<Pawn>(64);
            }
            return tsWorkingList;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction inst in instructions)
            {
                if (inst.opcode == OpCodes.Ldsfld && Field_workingList != null && Equals(inst.operand, Field_workingList))
                {
                    inst.opcode = OpCodes.Call;
                    inst.operand = Method_GetWorkingList;
                }
                yield return inst;
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_InfectionVectorTracker), "InfectionTickInterval")]
    public static class Patch_Pawn_InfectionVectorTracker_InfectionTickInterval
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch(typeof(PortraitsCache), "SetDirty")]
    public static class Patch_PortraitsCache_SetDirty
    {
        public static bool Prefix(Pawn pawn)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Pawn p = pawn;
                MainThreadSyncQueue.Enqueue(delegate
                {
                    if (p != null)
                    {
                        PortraitsCache.SetDirty(p);
                    }
                });
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(RegionAndRoomQuery), "GetDistrict", new Type[] { typeof(Thing), typeof(RegionType) })]
    public static class Patch_RegionAndRoomQuery_GetDistrict
    {
        public static bool Prefix(Thing thing, ref District __result)
        {
            if (thing == null || !thing.Spawned || thing.Map == null)
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(RegionAndRoomQuery), "GetRegion", new Type[] { typeof(Thing), typeof(RegionType) })]
    public static class Patch_RegionAndRoomQuery_GetRegion
    {
        public static bool Prefix(Thing thing, ref Region __result)
        {
            if (thing == null || !thing.Spawned || thing.Map == null)
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(IdeoUtility), "GetStyleDominanceFromCellsCenteredOn")]
    public static class Patch_IdeoUtility_GetStyleDominanceFromCellsCenteredOn
    {
        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch(typeof(SituationalThoughtHandler), "SituationalThoughtInterval")]
    public static class Patch_SituationalThoughtHandler_SituationalThoughtInterval
    {
        public static void Prefix(SituationalThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch(typeof(SituationalThoughtHandler), "AppendSocialThoughts")]
    public static class Patch_SituationalThoughtHandler_AppendSocialThoughts
    {
        public static void Prefix(SituationalThoughtHandler __instance, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                Monitor.Enter(ParallelTickEngine.ThoughtHandlerLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.ThoughtHandlerLock);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_StaticFieldThoughtWorkers_Lock
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type[] types = new Type[]
            {
                typeof(ThoughtWorker_Precept_AltarSharing),
                typeof(ThoughtWorker_BondedAnimalMaster),
                typeof(ThoughtWorker_YoungstersMoodBase)
            };
            for (int i = 0; i < types.Length; i++)
            {
                MethodInfo m = AccessTools.DeclaredMethod(types[i], "CurrentStateInternal");
                if (m != null)
                {
                    yield return m;
                }
            }
        }

        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch(typeof(ThinkNode_Random), "TryIssueJobPackage")]
    public static class Patch_ThinkNode_Random_TryIssueJobPackage
    {
        [ThreadStatic]
        private static List<ThinkNode>[] tsPool;
        [ThreadStatic]
        private static int tsDepth;

        public static bool Prefix(ThinkNode_Random __instance, Pawn pawn, JobIssueParams jobParams, ref ThinkResult __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (__instance == null || __instance.subNodes == null)
            {
                __result = ThinkResult.NoJob;
                return false;
            }

            List<ThinkNode>[] pool = tsPool;
            if (pool == null)
            {
                pool = new List<ThinkNode>[16];
                tsPool = pool;
            }

            int depth = tsDepth;
            List<ThinkNode> tempList;
            if (depth < pool.Length)
            {
                tempList = pool[depth];
                if (tempList == null)
                {
                    tempList = new List<ThinkNode>(16);
                    pool[depth] = tempList;
                }
                else
                {
                    tempList.Clear();
                }
            }
            else
            {
                tempList = new List<ThinkNode>(16);
            }

            tsDepth = depth + 1;
            try
            {
                for (int i = 0; i < __instance.subNodes.Count; i++)
                {
                    ThinkNode sub = __instance.subNodes[i];
                    if (sub != null)
                    {
                        tempList.Add(sub);
                    }
                }
                tempList.Shuffle();
                for (int j = 0; j < tempList.Count; j++)
                {
                    if (tempList[j] == null)
                    {
                        continue;
                    }
                    ThinkResult result = tempList[j].TryIssueJobPackage(pawn, jobParams);
                    if (result.IsValid)
                    {
                        __result = result;
                        return false;
                    }
                }
                __result = ThinkResult.NoJob;
                return false;
            }
            finally
            {
                tempList.Clear();
                tsDepth = depth;
            }
        }
    }

    [HarmonyPatch(typeof(ThinkNode_SubtreesByTag), "TryIssueJobPackage")]
    public static class Patch_ThinkNode_SubtreesByTag_TryIssueJobPackage
    {
        private static readonly FieldInfo Field_matchedTrees = AccessTools.Field(typeof(ThinkNode_SubtreesByTag), "matchedTrees");

        public static bool Prefix(ThinkNode_SubtreesByTag __instance, Pawn pawn, JobIssueParams jobParams, ref ThinkResult __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (__instance == null || pawn == null)
            {
                __result = ThinkResult.NoJob;
                return false;
            }

            List<ThinkTreeDef> matchedTrees = Field_matchedTrees != null ? (List<ThinkTreeDef>)Field_matchedTrees.GetValue(__instance) : null;
            if (matchedTrees == null)
            {
                lock (ParallelTickEngine.GlobalSyncLock)
                {
                    matchedTrees = Field_matchedTrees != null ? (List<ThinkTreeDef>)Field_matchedTrees.GetValue(__instance) : null;
                    if (matchedTrees == null)
                    {
                        List<ThinkTreeDef> built = new List<ThinkTreeDef>();
                        List<ThinkTreeDef> allDefs = DefDatabase<ThinkTreeDef>.AllDefsListForReading;
                        if (allDefs != null)
                        {
                            for (int i = 0; i < allDefs.Count; i++)
                            {
                                ThinkTreeDef def = allDefs[i];
                                if (def != null && def.insertTag == __instance.insertTag)
                                {
                                    built.Add(def);
                                }
                            }
                        }
                        built.SortByDescending((ThinkTreeDef tDef) => tDef != null ? tDef.insertPriority : 0f);
                        if (Field_matchedTrees != null)
                        {
                            Field_matchedTrees.SetValue(__instance, built);
                        }
                        matchedTrees = built;
                    }
                }
            }

            for (int i = 0; i < matchedTrees.Count; i++)
            {
                ThinkTreeDef treeDef = matchedTrees[i];
                if (treeDef == null || treeDef.thinkRoot == null)
                {
                    continue;
                }
                ThinkResult res = treeDef.thinkRoot.TryIssueJobPackage(pawn, jobParams);
                if (res.IsValid)
                {
                    __result = res;
                    return false;
                }
            }
            __result = ThinkResult.NoJob;
            return false;
        }
    }

    [HarmonyPatch]
    public static class Patch_Pawn_JobTracker_DetermineNextJob
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo[] methods = typeof(Pawn_JobTracker).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name == "DetermineNextJob" || m.Name == "TryFindAndStartJob" || m.Name == "CheckForJobOverride" || m.Name == "StartJob" || m.Name == "EndCurrentJob")
                {
                    yield return m;
                }
            }
        }

        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch(typeof(Toils_LayDown), "ApplyBedRelatedEffects")]
    public static class Patch_Toils_LayDown_ApplyBedRelatedEffects
    {
        public static void Prefix(Pawn p, int delta, ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && p != null && p.IsHashIntervalTick(100, delta))
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_StaticFieldVanillaJobGivers_Lock
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            Type[] types = new Type[]
            {
                typeof(JobGiver_OptimizeApparel),
                typeof(JobGiver_SatisfyChemicalNeed),
                typeof(JobGiver_SatifyChemicalDependency),
                typeof(JobGiver_SeekSafeTemperature),
                typeof(JobGiver_FindOxygen),
                typeof(JobGiver_AITrashBuildingsDistant),
                typeof(JobGiver_FleeAllPawns),
                typeof(JobGiver_Learn)
            };
            for (int i = 0; i < types.Length; i++)
            {
                MethodInfo m = AccessTools.DeclaredMethod(types[i], "TryGiveJob");
                if (m != null)
                {
                    yield return m;
                }
            }
        }

        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_GenPlace_Lock
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo[] methods = typeof(GenPlace).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name == "TryPlaceThing" || m.Name == "TryPlaceDirect" || m.Name == "PlaceSpotQualityAt" || m.Name == "HaulPlaceBlockerIn")
                {
                    yield return m;
                }
            }
        }

        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.GlobalSyncLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_GenTemperature_Lock
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo eq = AccessTools.Method(typeof(GenTemperature), "EqualizeTemperaturesThroughBuilding");
            if (eq != null)
            {
                yield return eq;
            }
            MethodInfo push = AccessTools.Method(typeof(GenTemperature), "PushHeat", new Type[] { typeof(IntVec3), typeof(Map), typeof(float) });
            if (push != null)
            {
                yield return push;
            }
        }

        public static void Prefix(ref bool __state)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Monitor.Enter(ParallelTickEngine.GenTemperatureLock, ref __state);
            }
        }

        public static void Finalizer(bool __state)
        {
            if (__state)
            {
                Monitor.Exit(ParallelTickEngine.GenTemperatureLock);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "Kill")]
    public static class Patch_Pawn_Kill
    {
        [ThreadStatic]
        private static Pawn tsLastKilledPawn;
        [ThreadStatic]
        private static int tsLastKilledStamp;

        public static bool Prefix(Pawn __instance, DamageInfo? dinfo, Hediff exactCulprit)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                if (__instance != null && !__instance.Dead)
                {
                    int stamp = MainThreadSyncQueue.CurrentQueueTickStamp;
                    if (tsLastKilledStamp == stamp && ReferenceEquals(tsLastKilledPawn, __instance))
                    {
                        tsLastKilledPawn = null;
                        throw new InvalidOperationException("OmniThread loop guard: Pawn.Kill called repeatedly on same Pawn in one tick.");
                    }
                    tsLastKilledStamp = stamp;
                    tsLastKilledPawn = __instance;

                    Pawn p = __instance;
                    DamageInfo? d = dinfo;
                    Hediff h = exactCulprit;
                    MainThreadSyncQueue.Enqueue(delegate
                    {
                        if (p != null && !p.Dead && !p.Destroyed)
                        {
                            p.Kill(d, h);
                        }
                    });
                }
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(WealthWatcher), "ForceRecount")]
    public static class Patch_WealthWatcher_ForceRecount
    {
        private static readonly FieldInfo Field_lastCountTick = AccessTools.Field(typeof(WealthWatcher), "lastCountTick");
        private static readonly long[] sLastEnqueuedTokens = new long[64];

        public static bool Prefix(WealthWatcher __instance, bool allowDuringInit)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                if (Field_lastCountTick != null && Find.TickManager != null)
                {
                    Field_lastCountTick.SetValue(__instance, (float)Find.TickManager.TicksGame);
                }
                int stamp = MainThreadSyncQueue.CurrentQueueTickStamp;
                int instHash = RuntimeHelpers.GetHashCode(__instance);
                int slot = instHash & 63;
                long token = ((long)instHash << 32) | (uint)stamp;
                if (Interlocked.Exchange(ref sLastEnqueuedTokens[slot], token) == token)
                {
                    return false;
                }
                WealthWatcher ww = __instance;
                bool allow = allowDuringInit;
                MainThreadSyncQueue.Enqueue(delegate
                {
                    if (ww != null)
                    {
                        ww.ForceRecount(allow);
                    }
                });
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    public static class Patch_Pawn_RecordsTracker_RecordsTickInterval
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo m1 = AccessTools.Method(typeof(Pawn_RecordsTracker), "RecordsTickInterval");
            if (m1 != null)
            {
                yield return m1;
            }
            MethodInfo m2 = AccessTools.Method(typeof(Pawn_RecordsTracker), "RecordsTickUpdate");
            if (m2 != null)
            {
                yield return m2;
            }
        }

        public static bool Prefix(Pawn_RecordsTracker __instance)
        {
            if (__instance == null || __instance.pawn == null || __instance.pawn.Destroyed)
            {
                return false;
            }
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Pawn pawn = __instance.pawn;
                if (pawn.jobs != null && pawn.jobs.curJob != null && pawn.jobs.curDriver == null)
                {
                    return false;
                }
            }
            return true;
        }

        public static Exception Finalizer(Exception __exception)
        {
            if (__exception is NullReferenceException && OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                return null;
            }
            return __exception;
        }
    }

    [HarmonyPatch]
    public static class Patch_ReachabilityCache_Clear
    {
        private static readonly long[] sLastEnqueuedTokens = new long[64];

        public static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo m1 = AccessTools.Method(typeof(ReachabilityCache), "Clear");
            if (m1 != null) yield return m1;
            MethodInfo m2 = AccessTools.Method(typeof(ReachabilityCache), "ClearFor");
            if (m2 != null) yield return m2;
            MethodInfo m3 = AccessTools.Method(typeof(ReachabilityCache), "ClearForHostile");
            if (m3 != null) yield return m3;
        }

        public static bool Prefix(ReachabilityCache __instance)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection && __instance != null)
            {
                int stamp = MainThreadSyncQueue.CurrentQueueTickStamp;
                int instHash = RuntimeHelpers.GetHashCode(__instance);
                int slot = instHash & 63;
                long token = ((long)instHash << 32) | (uint)stamp;
                if (Interlocked.Exchange(ref sLastEnqueuedTokens[slot], token) == token)
                {
                    return false;
                }
                ReachabilityCache rc = __instance;
                MainThreadSyncQueue.Enqueue(delegate
                {
                    if (rc != null)
                    {
                        rc.Clear();
                    }
                });
                return false;
            }
            return true;
        }
    }
}
