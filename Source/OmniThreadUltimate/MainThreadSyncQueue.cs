using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace OmniThreadUltimate
{
    public static class MainThreadSyncQueue
    {
        public static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        public static readonly ConcurrentQueue<string> RecentExceptions = new ConcurrentQueue<string>();

        public static int MainThreadId = -1;
        public static volatile bool InParallelSection = false;

        public static Sustainer DummySustainer;
        public static MoteBubble DummyMoteBubble;
        private static FieldInfo SustainerEndRealTimeField;
        private static FieldInfo SustainerLastMaintainTickField;

        public static Sustainer GetDummySustainer()
        {
            if (DummySustainer == null)
            {
                try
                {
                    Sustainer dummy = (Sustainer)FormatterServices.GetUninitializedObject(typeof(Sustainer));
                    if (SustainerEndRealTimeField == null)
                    {
                        SustainerEndRealTimeField = typeof(Sustainer).GetField("endRealTime", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    }
                    if (SustainerEndRealTimeField != null)
                    {
                        SustainerEndRealTimeField.SetValue(dummy, -1f);
                    }
                    dummy.externalParams = new SoundParams();
                    dummy.scopeFader = new SustainerScopeFader();
                    dummy.def = new SoundDef();
                    FieldInfo subSustainersField = typeof(Sustainer).GetField("subSustainers", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (subSustainersField != null)
                    {
                        subSustainersField.SetValue(dummy, new List<SubSustainer>());
                    }
                    DummySustainer = dummy;
                }
                catch
                {
                }
            }
            return DummySustainer;
        }

        public static void SetSustainerMaintainTickSafe(Sustainer sustainer, int tick)
        {
            if (sustainer == null || ReferenceEquals(sustainer, DummySustainer))
            {
                return;
            }
            try
            {
                if (SustainerLastMaintainTickField == null)
                {
                    SustainerLastMaintainTickField = typeof(Sustainer).GetField("lastMaintainTick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if (SustainerLastMaintainTickField != null)
                {
                    SustainerLastMaintainTickField.SetValue(sustainer, tick);
                }
            }
            catch
            {
            }
        }

        public static MoteBubble GetDummyMoteBubble()
        {
            if (DummyMoteBubble == null)
            {
                try
                {
                    MoteBubble dummy = (MoteBubble)FormatterServices.GetUninitializedObject(typeof(MoteBubble));
                    dummy.def = new ThingDef();
                    DummyMoteBubble = dummy;
                }
                catch
                {
                }
            }
            return DummyMoteBubble;
        }

        public static long TotalEnqueuedActions = 0;
        public static long TotalFlushedActions = 0;
        public static int EnqueuedThisTick = 0;
        public static long CaughtConcurrencyExceptions = 0;
        public static long PreventedThreadHazards = 0;

        public const int MaxEnqueuedPerTick = 4096;
        public const int MaxThreadEnqueuedPerTick = 1024;

        [ThreadStatic]
        private static int tsEnqueuedThisTick;
        [ThreadStatic]
        private static int tsLastTickStamp;

        public static int CurrentQueueTickStamp = 1;

        public static bool IsMainThread
        {
            get
            {
                return Thread.CurrentThread.ManagedThreadId == MainThreadId;
            }
        }

        public static void InitializeOnMainThread()
        {
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
            GetDummySustainer();
            GetDummyMoteBubble();
        }

        public static void Enqueue(Action action)
        {
            if (action == null)
            {
                return;
            }

            int totalThisTick = Interlocked.Increment(ref EnqueuedThisTick);
            if (!IsMainThread && InParallelSection)
            {
                int stamp = CurrentQueueTickStamp;
                if (tsLastTickStamp != stamp)
                {
                    tsLastTickStamp = stamp;
                    tsEnqueuedThisTick = 0;
                }
                int threadCount = ++tsEnqueuedThisTick;
                if (threadCount > MaxThreadEnqueuedPerTick)
                {
                    tsEnqueuedThisTick = 0;
                    throw new InvalidOperationException("OmniThread circuit breaker: worker thread enqueued >1024 actions in a single tick (runaway loop aborted).");
                }
            }

            if (totalThisTick > MaxEnqueuedPerTick)
            {
                if (!IsMainThread && InParallelSection)
                {
                    tsEnqueuedThisTick = 0;
                    throw new InvalidOperationException("OmniThread circuit breaker: total enqueued actions exceeded 4096 in a single tick.");
                }
                return;
            }

            Queue.Enqueue(action);
            Interlocked.Increment(ref TotalEnqueuedActions);
            Interlocked.Increment(ref PreventedThreadHazards);
        }

        public static void Flush()
        {
            if (!IsMainThread)
            {
                return;
            }
            CurrentQueueTickStamp++;
            EnqueuedThisTick = 0;
            Action action;
            int safetyLimit = 8192;
            while (safetyLimit-- > 0 && Queue.TryDequeue(out action))
            {
                try
                {
                    action();
                    Interlocked.Increment(ref TotalFlushedActions);
                }
                catch (Exception ex)
                {
                    RecordWorkerException("MainThreadSyncQueue.Flush", ex);
                }
            }
            while (Queue.TryDequeue(out action))
            {
            }
        }

        private static string ExtractRawCallStack(Exception ex)
        {
            if (ex == null)
            {
                return "";
            }
            try
            {
                System.Diagnostics.StackTrace trace = new System.Diagnostics.StackTrace(ex, false);
                System.Diagnostics.StackFrame[] frames = trace.GetFrames();
                if (frames != null && frames.Length > 0)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    int count = Math.Min(frames.Length, 10);
                    for (int i = 0; i < count; i++)
                    {
                        System.Reflection.MethodBase mb = frames[i].GetMethod();
                        if (mb != null)
                        {
                            if (sb.Length > 0)
                            {
                                sb.Append(" <- ");
                            }
                            sb.Append(mb.DeclaringType != null ? mb.DeclaringType.Name : "?");
                            sb.Append(".");
                            sb.Append(mb.Name);
                        }
                    }
                    return sb.ToString();
                }
            }
            catch
            {
            }
            return ex.StackTrace != null ? ex.StackTrace.Replace("\r", "").Replace("\n", " | ") : "";
        }

        public static void RecordWorkerException(string context, Exception ex)
        {
            Interlocked.Increment(ref CaughtConcurrencyExceptions);
            if (RecentExceptions.Count < 50)
            {
                string st = ExtractRawCallStack(ex);
                string formatted = string.Format("[OmniThread Exception][Thread {0}][{1}] {2}: {3} >> {4}",
                    Thread.CurrentThread.ManagedThreadId,
                    context,
                    ex != null ? ex.GetType().FullName : "UnknownException",
                    ex != null ? ex.Message : "",
                    st);
                RecentExceptions.Enqueue(formatted);
            }
        }

        public static void RecordWorkerLogError(string text)
        {
            Interlocked.Increment(ref PreventedThreadHazards);
            if (!string.IsNullOrEmpty(text) &&
                (text.IndexOf("OmniThread loop guard:", StringComparison.Ordinal) >= 0 ||
                 text.IndexOf("Can't start LayDown toil", StringComparison.Ordinal) >= 0 ||
                 text.IndexOf("Could not resolve symbol", StringComparison.Ordinal) >= 0))
            {
                return;
            }
            if (RecentExceptions.Count < 50 && !string.IsNullOrEmpty(text))
            {
                string clean = text.Replace("\r", "").Replace("\n", " | ");
                string formatted = string.Format("[OmniThread WorkerLog][Thread {0}] {1}",
                    Thread.CurrentThread.ManagedThreadId,
                    clean.Length > 1200 ? clean.Substring(0, 1200) : clean);
                RecentExceptions.Enqueue(formatted);
            }
        }
    }

    [HarmonyPatch(typeof(Log), "TryOpenLogWindow")]
    public static class Patch_Log_TryOpenLogWindow
    {
        public static bool Prefix()
        {
            if (MainThreadSyncQueue.InParallelSection || !MainThreadSyncQueue.IsMainThread)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Log), "Error", new Type[] { typeof(string) })]
    public static class Patch_Log_Error
    {
        public static bool Prefix(string text)
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                MainThreadSyncQueue.RecordWorkerLogError(text);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Log), "Warning", new Type[] { typeof(string) })]
    public static class Patch_Log_Warning
    {
        public static bool Prefix()
        {
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                Interlocked.Increment(ref MainThreadSyncQueue.PreventedThreadHazards);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Log), "Message", new Type[] { typeof(string) })]
    public static class Patch_Log_Message
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

    [HarmonyPatch(typeof(Thing), "SpawnSetup")]
    public static class Patch_Thing_SpawnSetup
    {
        [ThreadStatic]
        private static Thing tsLastSpawnSetupThing;
        [ThreadStatic]
        private static int tsLastSpawnSetupStamp;

        public static bool Prefix(Thing __instance, Map map, bool respawningAfterLoad)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (__instance == null || map == null)
            {
                return false;
            }
            int stamp = MainThreadSyncQueue.CurrentQueueTickStamp;
            if (tsLastSpawnSetupStamp == stamp && ReferenceEquals(tsLastSpawnSetupThing, __instance))
            {
                tsLastSpawnSetupThing = null;
                throw new InvalidOperationException("OmniThread loop guard: Thing.SpawnSetup called repeatedly on same Thing in one tick.");
            }
            tsLastSpawnSetupStamp = stamp;
            tsLastSpawnSetupThing = __instance;

            Thing thingRef = __instance;
            Map mapRef = map;
            bool respawnRef = respawningAfterLoad;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (thingRef != null && !thingRef.Destroyed && !thingRef.Spawned && mapRef != null)
                {
                    thingRef.SpawnSetup(mapRef, respawnRef);
                }
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(Thing), "CanStackWith")]
    public static class Patch_Thing_CanStackWith
    {
        public static bool Prefix(Thing __instance, Thing other, ref bool __result)
        {
            if (__instance == null || other == null || ReferenceEquals(__instance, other) || __instance.stackCount <= 0 || other.stackCount <= 0)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(JobDriver), "ReadyForNextToil")]
    public static class Patch_JobDriver_ReadyForNextToil
    {
        [ThreadStatic]
        private static JobDriver tsLastDriver;
        [ThreadStatic]
        private static int tsLastStamp;
        [ThreadStatic]
        private static int tsTransitionCount;

        public static bool Prefix(JobDriver __instance)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (__instance == null)
            {
                return false;
            }
            int stamp = MainThreadSyncQueue.CurrentQueueTickStamp;
            if (tsLastStamp == stamp && ReferenceEquals(tsLastDriver, __instance))
            {
                tsTransitionCount++;
                if (tsTransitionCount > 24)
                {
                    tsLastDriver = null;
                    tsTransitionCount = 0;
                    Interlocked.Increment(ref MainThreadSyncQueue.PreventedThreadHazards);
                    return false;
                }
            }
            else
            {
                tsLastStamp = stamp;
                tsLastDriver = __instance;
                tsTransitionCount = 1;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(JobDriver), "DriverTickInterval")]
    public static class Patch_JobDriver_DriverTickInterval
    {
        public static bool Prefix(JobDriver __instance)
        {
            if (__instance == null || __instance.CurToilIndex < 0 || __instance.pawn == null || __instance.job == null || __instance.pawn.CurJob != __instance.job)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(CompIngredients), "AllowStackWith")]
    public static class Patch_CompIngredients_AllowStackWith
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

    [HarmonyPatch(typeof(CompIngredients), "MergeIngredients")]
    public static class Patch_CompIngredients_MergeIngredients
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

    [HarmonyPatch(typeof(CompIngredients), "RegisterIngredient")]
    public static class Patch_CompIngredients_RegisterIngredient
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

    [HarmonyPatch(typeof(CompIngredients), "PreAbsorbStack")]
    public static class Patch_CompIngredients_PreAbsorbStack
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

    [HarmonyPatch(typeof(CompIngredients), "PostSplitOff")]
    public static class Patch_CompIngredients_PostSplitOff
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

    [HarmonyPatch(typeof(Thing), "DeSpawn")]
    public static class Patch_Thing_DeSpawn
    {
        [ThreadStatic]
        private static Thing tsLastDeSpawnedThing;
        [ThreadStatic]
        private static int tsLastDeSpawnedStamp;
        [ThreadStatic]
        private static int tsLastDeSpawnedCount;

        public static bool Prefix(Thing __instance, DestroyMode mode)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (__instance == null || !__instance.Spawned)
            {
                return false;
            }
            int stamp = MainThreadSyncQueue.CurrentQueueTickStamp;
            if (tsLastDeSpawnedStamp == stamp && ReferenceEquals(tsLastDeSpawnedThing, __instance))
            {
                tsLastDeSpawnedCount++;
                if (tsLastDeSpawnedCount > 2)
                {
                    tsLastDeSpawnedThing = null;
                    tsLastDeSpawnedCount = 0;
                    throw new InvalidOperationException("OmniThread loop guard: Thing.DeSpawn called repeatedly on same Thing in one tick.");
                }
                return false;
            }
            tsLastDeSpawnedStamp = stamp;
            tsLastDeSpawnedThing = __instance;
            tsLastDeSpawnedCount = 1;

            Thing thingRef = __instance;
            DestroyMode modeRef = mode;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (thingRef != null && thingRef.Spawned)
                {
                    thingRef.DeSpawn(modeRef);
                }
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(Thing), "Destroy")]
    public static class Patch_Thing_Destroy
    {
        [ThreadStatic]
        private static Thing tsLastDestroyedThing;
        [ThreadStatic]
        private static int tsLastDestroyedStamp;

        public static bool Prefix(Thing __instance, DestroyMode mode)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (__instance == null || __instance.Destroyed)
            {
                return false;
            }
            if (__instance.holdingOwner != null)
            {
                lock (ParallelTickEngine.GlobalSyncLock)
                {
                    if (__instance.holdingOwner != null)
                    {
                        __instance.holdingOwner.Remove(__instance);
                    }
                }
            }
            int stamp = MainThreadSyncQueue.CurrentQueueTickStamp;
            if (tsLastDestroyedStamp == stamp && ReferenceEquals(tsLastDestroyedThing, __instance))
            {
                tsLastDestroyedThing = null;
                throw new InvalidOperationException("OmniThread loop guard: Thing.Destroy called repeatedly on same Thing in one tick.");
            }
            tsLastDestroyedStamp = stamp;
            tsLastDestroyedThing = __instance;

            Thing thingRef = __instance;
            DestroyMode modeRef = mode;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (thingRef != null && !thingRef.Destroyed)
                {
                    thingRef.Destroy(modeRef);
                }
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(GenSpawn), "Spawn", new Type[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(WipeMode), typeof(bool), typeof(bool) })]
    public static class Patch_GenSpawn_Spawn
    {
        [ThreadStatic]
        private static Thing tsLastSpawnedThing;
        [ThreadStatic]
        private static int tsLastSpawnedStamp;

        public static bool Prefix(Thing newThing, IntVec3 loc, Map map, Rot4 rot, WipeMode wipeMode, bool respawningAfterLoad, bool forbidLeavings, ref Thing __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (newThing == null || map == null)
            {
                __result = null;
                return false;
            }
            if (newThing.holdingOwner != null)
            {
                lock (ParallelTickEngine.GlobalSyncLock)
                {
                    if (newThing.holdingOwner != null)
                    {
                        newThing.holdingOwner.Remove(newThing);
                    }
                }
            }
            int stamp = MainThreadSyncQueue.CurrentQueueTickStamp;
            if (tsLastSpawnedStamp == stamp && ReferenceEquals(tsLastSpawnedThing, newThing))
            {
                tsLastSpawnedThing = null;
                throw new InvalidOperationException("OmniThread loop guard: GenSpawn.Spawn called repeatedly on same Thing in one tick.");
            }
            tsLastSpawnedStamp = stamp;
            tsLastSpawnedThing = newThing;

            if (!newThing.Spawned)
            {
                newThing.Rotation = rot;
                newThing.Position = loc;
            }
            Thing thingRef = newThing;
            IntVec3 locRef = loc;
            Map mapRef = map;
            Rot4 rotRef = rot;
            WipeMode wipeRef = wipeMode;
            bool respawnRef = respawningAfterLoad;
            bool forbidRef = forbidLeavings;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (thingRef != null && !thingRef.Destroyed && !thingRef.Spawned && mapRef != null && locRef.InBounds(mapRef))
                {
                    GenSpawn.Spawn(thingRef, locRef, mapRef, rotRef, wipeRef, respawnRef, forbidRef);
                }
            });
            __result = newThing;
            return false;
        }
    }

    [HarmonyPatch(typeof(SoundStarter), "TrySpawnSustainer")]
    public static class Patch_SoundStarter_TrySpawnSustainer
    {
        public static bool Prefix(ref Sustainer __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            Interlocked.Increment(ref MainThreadSyncQueue.PreventedThreadHazards);
            __result = MainThreadSyncQueue.GetDummySustainer();
            return false;
        }
    }

    [HarmonyPatch(typeof(Sustainer), "Maintain")]
    public static class Patch_Sustainer_Maintain
    {
        public static bool Prefix(Sustainer __instance)
        {
            if (__instance == null || ReferenceEquals(__instance, MainThreadSyncQueue.DummySustainer))
            {
                return false;
            }
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (Find.TickManager != null)
            {
                MainThreadSyncQueue.SetSustainerMaintainTickSafe(__instance, Find.TickManager.TicksGame);
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(Sustainer), "End")]
    public static class Patch_Sustainer_End
    {
        public static bool Prefix(Sustainer __instance)
        {
            if (__instance == null || ReferenceEquals(__instance, MainThreadSyncQueue.DummySustainer))
            {
                return false;
            }
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            Sustainer sustRef = __instance;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (sustRef != null && !sustRef.Ended)
                {
                    sustRef.End();
                }
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(SoundParams), "set_Item")]
    public static class Patch_SoundParams_set_Item
    {
        public static bool Prefix(SoundParams __instance)
        {
            if (__instance == null || (MainThreadSyncQueue.DummySustainer != null && ReferenceEquals(__instance, MainThreadSyncQueue.DummySustainer.externalParams)))
            {
                return false;
            }
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(MoteBubble), "SetupMoteBubble")]
    public static class Patch_MoteBubble_SetupMoteBubble
    {
        public static bool Prefix(MoteBubble __instance)
        {
            if (__instance == null || ReferenceEquals(__instance, MainThreadSyncQueue.DummyMoteBubble))
            {
                return false;
            }
            if (OmniThreadMod.EnableMultithreading && MainThreadSyncQueue.InParallelSection)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Ability), "MoteTick")]
    public static class Patch_Ability_MoteTick
    {
        private static Action<Ability> MoteTickDelegate;
        private static bool DelegateInitialized;

        public static bool Prefix(Ability __instance)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            if (__instance == null || !__instance.Casting)
            {
                return false;
            }
            if (!DelegateInitialized)
            {
                DelegateInitialized = true;
                try
                {
                    MethodInfo mi = typeof(Ability).GetMethod("MoteTick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (mi != null)
                    {
                        MoteTickDelegate = (Action<Ability>)Delegate.CreateDelegate(typeof(Action<Ability>), mi);
                    }
                }
                catch
                {
                }
            }
            Ability abRef = __instance;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (abRef != null && abRef.Casting && MoteTickDelegate != null)
                {
                    MoteTickDelegate(abRef);
                }
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(FleckManager), "CreateFleck")]
    public static class Patch_FleckManager_CreateFleck
    {
        public static bool Prefix(FleckManager __instance, FleckCreationData fleckData)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            FleckManager mgrRef = __instance;
            FleckCreationData dataRef = fleckData;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (mgrRef != null)
                {
                    mgrRef.CreateFleck(dataRef);
                }
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_InteractionsTracker), "TryInteractWith")]
    public static class Patch_Pawn_InteractionsTracker_TryInteractWith
    {
        public static bool Prefix(Pawn_InteractionsTracker __instance, Pawn recipient, InteractionDef intDef, ref bool __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            Pawn_InteractionsTracker trackerRef = __instance;
            Pawn recipientRef = recipient;
            InteractionDef defRef = intDef;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (trackerRef != null && recipientRef != null && defRef != null)
                {
                    trackerRef.TryInteractWith(recipientRef, defRef);
                }
            });
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(MoteMaker), "ThrowText", new Type[] { typeof(Vector3), typeof(Map), typeof(string), typeof(Color), typeof(float) })]
    public static class Patch_MoteMaker_ThrowText
    {
        public static bool Prefix(Vector3 loc, Map map, string text, Color color, float timeBeforeStartFadeout)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            Vector3 locRef = loc;
            Map mapRef = map;
            string textRef = text;
            Color colorRef = color;
            float fadeRef = timeBeforeStartFadeout;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (mapRef != null)
                {
                    MoteMaker.ThrowText(locRef, mapRef, textRef, colorRef, fadeRef);
                }
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(MoteMaker), "MakeMoodThoughtBubble")]
    public static class Patch_MoteMaker_MakeMoodThoughtBubble
    {
        public static bool Prefix(Pawn pawn, Thought thought, ref MoteBubble __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            Pawn pawnRef = pawn;
            Thought thoughtRef = thought;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (pawnRef != null && thoughtRef != null && pawnRef.Spawned)
                {
                    MoteMaker.MakeMoodThoughtBubble(pawnRef, thoughtRef);
                }
            });
            __result = MainThreadSyncQueue.GetDummyMoteBubble();
            return false;
        }
    }

    [HarmonyPatch(typeof(MoteMaker), "MakeThoughtBubble")]
    public static class Patch_MoteMaker_MakeThoughtBubble
    {
        public static bool Prefix(Pawn pawn, string iconPath, bool maintain, ref MoteBubble __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            Pawn pawnRef = pawn;
            string iconRef = iconPath;
            bool maintainRef = maintain;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (pawnRef != null && pawnRef.Spawned)
                {
                    MoteMaker.MakeThoughtBubble(pawnRef, iconRef, maintainRef);
                }
            });
            __result = MainThreadSyncQueue.GetDummyMoteBubble();
            return false;
        }
    }

    [HarmonyPatch(typeof(MoteMaker), "MakeInteractionBubble")]
    public static class Patch_MoteMaker_MakeInteractionBubble
    {
        public static bool Prefix(ref MoteBubble __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            Interlocked.Increment(ref MainThreadSyncQueue.PreventedThreadHazards);
            __result = MainThreadSyncQueue.GetDummyMoteBubble();
            return false;
        }
    }

    [HarmonyPatch(typeof(MoteMaker), "MakeSpeechBubble")]
    public static class Patch_MoteMaker_MakeSpeechBubble
    {
        public static bool Prefix(ref MoteBubble __result)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            Interlocked.Increment(ref MainThreadSyncQueue.PreventedThreadHazards);
            __result = MainThreadSyncQueue.GetDummyMoteBubble();
            return false;
        }
    }

    [HarmonyPatch(typeof(MoteMaker), "MakeColonistActionOverlay")]
    public static class Patch_MoteMaker_MakeColonistActionOverlay
    {
        public static bool Prefix(Pawn pawn, ThingDef moteDef)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            Pawn pawnRef = pawn;
            ThingDef defRef = moteDef;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (pawnRef != null && defRef != null && pawnRef.Spawned)
                {
                    MoteMaker.MakeColonistActionOverlay(pawnRef, defRef);
                }
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(ResourceCounter), "UpdateResourceCounts")]
    public static class Patch_ResourceCounter_UpdateResourceCounts
    {
        public static bool Prefix(ResourceCounter __instance)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            ResourceCounter rcRef = __instance;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (rcRef != null)
                {
                    rcRef.UpdateResourceCounts();
                }
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(ResourceCounter), "ResetResourceCounts")]
    public static class Patch_ResourceCounter_ResetResourceCounts
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

    [HarmonyPatch(typeof(ResourceCounter), "GetCount")]
    public static class Patch_ResourceCounter_GetCount
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

    [HarmonyPatch(typeof(RestUtility), "TuckIntoBed")]
    public static class Patch_RestUtility_TuckIntoBed
    {
        public static bool Prefix(Building_Bed bed, Pawn taker, Pawn takee, bool rescued)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            Building_Bed bedRef = bed;
            Pawn takerRef = taker;
            Pawn takeeRef = takee;
            bool rescuedRef = rescued;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (bedRef != null && takerRef != null && takeeRef != null)
                {
                    RestUtility.TuckIntoBed(bedRef, takerRef, takeeRef, rescuedRef);
                }
            });
            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn), "Notify_Teleported")]
    public static class Patch_Pawn_Notify_Teleported
    {
        public static bool Prefix(Pawn __instance, bool endCurrentJob, bool resetTweenedPos)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
            Pawn pawnRef = __instance;
            bool endJobRef = endCurrentJob;
            bool resetPosRef = resetTweenedPos;
            MainThreadSyncQueue.Enqueue(delegate
            {
                if (pawnRef != null && pawnRef.Spawned)
                {
                    pawnRef.Notify_Teleported(endJobRef, resetPosRef);
                }
            });
            return false;
        }
    }
}
