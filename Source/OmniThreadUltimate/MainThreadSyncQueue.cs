using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace OmniThreadUltimate
{
    public static class MainThreadSyncQueue
    {
        public static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
        public static readonly ConcurrentQueue<string> RecentExceptions = new ConcurrentQueue<string>();

        public static int MainThreadId = -1;
        public static volatile bool InParallelSection = false;

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
            if (RecentExceptions.Count < 50 && !string.IsNullOrEmpty(text))
            {
                string clean = text.Replace("\r", "").Replace("\n", " | ");
                string formatted = string.Format("[OmniThread WorkerLog][Thread {0}] {1}",
                    Thread.CurrentThread.ManagedThreadId,
                    clean.Length > 380 ? clean.Substring(0, 380) : clean);
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
        public static bool Prefix(Thing __instance, Map map, bool respawningAfterLoad)
        {
            if (!OmniThreadMod.EnableMultithreading || !MainThreadSyncQueue.InParallelSection)
            {
                return true;
            }
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

    [HarmonyPatch(typeof(Thing), "DeSpawn")]
    public static class Patch_Thing_DeSpawn
    {
        [ThreadStatic]
        private static Thing tsLastDeSpawnedThing;
        [ThreadStatic]
        private static int tsLastDeSpawnedStamp;

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
                tsLastDeSpawnedThing = null;
                throw new InvalidOperationException("OmniThread loop guard: Thing.DeSpawn called repeatedly on same Thing in one tick.");
            }
            tsLastDeSpawnedStamp = stamp;
            tsLastDeSpawnedThing = __instance;

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
            __result = null;
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
            __result = null;
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
            __result = null;
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
            __result = null;
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
            __result = null;
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
}
