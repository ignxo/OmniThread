using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace OmniThreadUltimate
{
    public static class OmniThreadGUI
    {
        public static bool ShowOverlay = false;
        private static int lastFrameTicksGame = -1;
        private static float lastTpsSampleTime = 0f;
        public static float RealWallTps = 0f;

        private static readonly List<KeyValuePair<int, long>> SortedThreads = new List<KeyValuePair<int, long>>(32);

        public static void DrawTelemetryOverlay()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.TickManager == null)
            {
                return;
            }

            if (Event.current != null && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.F10)
            {
                ShowOverlay = !ShowOverlay;
                Event.current.Use();
            }

            if (!ShowOverlay)
            {
                return;
            }

            UpdateWallTpsCounter();

            float panelWidth = 390f;
            float panelHeight = AutoBenchmarkRunner.IsAutoTestMode && !AutoBenchmarkRunner.CaptureComparisonMode ? 372f : 352f;
            Rect panelRect = new Rect(170f, 140f, panelWidth, panelHeight);

            Widgets.DrawBoxSolid(panelRect, new Color(0.06f, 0.08f, 0.12f, 0.88f));
            GUI.color = new Color(0.2f, 0.75f, 0.95f, 0.9f);
            Widgets.DrawBox(panelRect, 1);
            GUI.color = Color.white;

            GameFont prevFont = Text.Font;
            TextAnchor prevAnchor = Text.Anchor;

            Text.Font = GameFont.Small;
            float curY = panelRect.y + 8f;
            float leftX = panelRect.x + 10f;
            float innerWidth = panelWidth - 20f;

            GUI.color = new Color(0.3f, 0.9f, 1f);
            Widgets.Label(new Rect(leftX, curY, innerWidth - 85f, 22f),
                OmniThreadMod.EnableMultithreading ? "OMNITHREAD ULTIMATE [12T ON]" : "OMNITHREAD ULTIMATE [1T OFF]");
            GUI.color = Color.white;

            Rect toggleHudBtn = new Rect(panelRect.xMax - 82f, curY, 74f, 20f);
            if (Widgets.ButtonText(toggleHudBtn, "Hide [F10]"))
            {
                ShowOverlay = false;
            }
            curY += 24f;

            float avgMs = ParallelTickEngine.RollingAvgTickMs;
            float computeTps = avgMs > 0.001f ? (1000f / avgMs) : 0f;
            int pawnCount = Find.CurrentMap != null ? Find.CurrentMap.mapPawns.AllPawnsSpawnedCount : 0;

            GUI.color = computeTps >= 200f ? new Color(0.35f, 1f, 0.45f) : (computeTps >= 60f ? Color.yellow : new Color(1f, 0.4f, 0.4f));
            Widgets.Label(new Rect(leftX, curY, innerWidth, 22f),
                string.Format(CultureInfo.InvariantCulture, "Compute TPS: {0:F1}  |  Wall TPS: {1:F1}", computeTps, RealWallTps));
            GUI.color = Color.white;
            curY += 20f;

            Widgets.Label(new Rect(leftX, curY, innerWidth, 22f),
                string.Format(CultureInfo.InvariantCulture, "Tick: {0:F2} ms (TickList: {1:F2} ms | Last: {2:F2} ms)", avgMs, ParallelTickEngine.RollingAvgTickListMs, ParallelTickEngine.LastTickMs));
            curY += 20f;

            Widgets.Label(new Rect(leftX, curY, innerWidth, 22f),
                string.Format("Active Threads: {0}/{1}  |  Map Pawns: {2}",
                    OmniThreadMod.EnableMultithreading ? ParallelTickEngine.ActiveWorkerThreadsLastTick : 1,
                    Environment.ProcessorCount,
                    pawnCount));
            curY += 20f;

            Widgets.Label(new Rect(leftX, curY, innerWidth, 22f),
                string.Format("Mod Compat: {0} Mods | {1} Patches | {2} MT-Routed",
                    ModCompatibilityManager.DetectedActiveModsCount,
                    ModCompatibilityManager.AppliedModCompatPatchesCount,
                    ModCompatibilityManager.MainThreadRoutedModThingTicks));
            curY += 20f;

            Widgets.Label(new Rect(leftX, curY, innerWidth, 22f),
                string.Format("SyncQueue Flushed: {0} | Hazards Guarded: {1} | Err: {2}",
                    MainThreadSyncQueue.TotalFlushedActions,
                    MainThreadSyncQueue.PreventedThreadHazards,
                    MainThreadSyncQueue.CaughtConcurrencyExceptions));
            curY += 22f;

            Rect modeBtnRect = new Rect(leftX, curY, innerWidth, 20f);
            string btnLabel = OmniThreadMod.EnableMultithreading ? "Mode: MULTI-THREADED (Click to toggle)" : "Mode: VANILLA SINGLE-THREAD (Click to toggle)";
            if (Widgets.ButtonText(modeBtnRect, btnLabel))
            {
                OmniThreadMod.EnableMultithreading = !OmniThreadMod.EnableMultithreading;
                ParallelTickEngine.ResetThreadWorkCounts();
                ResetWallTpsCounter();
                if (OmniThreadModController.Settings != null)
                {
                    OmniThreadModController.Settings.EnableMultithreading = OmniThreadMod.EnableMultithreading;
                    OmniThreadModController.Settings.Write();
                }
            }
            curY += 22f;

            Rect speedCapBtnRect = new Rect(leftX, curY, innerWidth, 20f);
            string speedCapLabel = OmniThreadMod.UncapMaxTimeSpeed ? "Max Speed (>>>): UNCAPPED (Click to cap 360)" : "Max Speed (>>>): 360 TPS CAP (Click to uncap)";
            if (Widgets.ButtonText(speedCapBtnRect, speedCapLabel))
            {
                OmniThreadMod.UncapMaxTimeSpeed = !OmniThreadMod.UncapMaxTimeSpeed;
                if (OmniThreadModController.Settings != null)
                {
                    OmniThreadModController.Settings.UncapMaxTimeSpeed = OmniThreadMod.UncapMaxTimeSpeed;
                    OmniThreadModController.Settings.Write();
                }
            }
            curY += 22f;

            if (AutoBenchmarkRunner.IsAutoTestMode && !AutoBenchmarkRunner.CaptureComparisonMode)
            {
                float progress = Mathf.Clamp01((float)AutoBenchmarkRunner.RecordedTicks / (float)AutoBenchmarkRunner.TargetBenchmarkTicks);
                Rect barRect = new Rect(leftX, curY, innerWidth, 16f);
                Widgets.FillableBar(barRect, progress);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(barRect, string.Format("AUTOTEST: {0} / {1} Ticks", AutoBenchmarkRunner.RecordedTicks, AutoBenchmarkRunner.TargetBenchmarkTicks));
                Text.Anchor = TextAnchor.UpperLeft;
                curY += 20f;
            }

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(leftX, curY, innerWidth, 16f), "Per-Thread Work Distribution (Top 8 Worker Threads):");
            curY += 16f;

            ParallelTickEngine.SyncThreadWorkDictionary();
            SortedThreads.Clear();
            long maxWork = 1L;
            foreach (KeyValuePair<int, long> kvp in ParallelTickEngine.ThreadWorkCounts)
            {
                SortedThreads.Add(kvp);
                if (kvp.Value > maxWork)
                {
                    maxWork = kvp.Value;
                }
            }
            SortedThreads.Sort(delegate(KeyValuePair<int, long> a, KeyValuePair<int, long> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            int barsToShow = Math.Min(7, SortedThreads.Count);
            for (int i = 0; i < barsToShow; i++)
            {
                KeyValuePair<int, long> item = SortedThreads[i];
                float ratio = Mathf.Clamp01((float)item.Value / (float)maxWork);
                Rect labelRect = new Rect(leftX, curY, 90f, 14f);
                string tTag = (item.Key == MainThreadSyncQueue.MainThreadId) ? string.Format("T#{0} (Main)", item.Key) : string.Format("T#{0} (Work)", item.Key);
                Widgets.Label(labelRect, tTag);

                Rect barBg = new Rect(leftX + 92f, curY + 2f, innerWidth - 155f, 10f);
                Widgets.DrawBoxSolid(barBg, new Color(0.18f, 0.2f, 0.25f, 0.9f));
                Rect barFill = new Rect(barBg.x, barBg.y, barBg.width * ratio, barBg.height);
                Widgets.DrawBoxSolid(barFill, item.Key == MainThreadSyncQueue.MainThreadId ? new Color(0.95f, 0.65f, 0.2f, 0.9f) : new Color(0.25f, 0.85f, 0.55f, 0.9f));

                Rect valRect = new Rect(barBg.xMax + 4f, curY, 60f, 14f);
                Widgets.Label(valRect, item.Value.ToString());
                curY += 15f;
            }

            Text.Font = prevFont;
            Text.Anchor = prevAnchor;
            GUI.color = Color.white;
        }

        public static void ResetWallTpsCounter()
        {
            lastFrameTicksGame = -1;
            lastTpsSampleTime = 0f;
            RealWallTps = 0f;
        }

        private static void UpdateWallTpsCounter()
        {
            float now = Time.realtimeSinceStartup;
            int curTicks = Find.TickManager.TicksGame;
            if (lastFrameTicksGame < 0)
            {
                lastFrameTicksGame = curTicks;
                lastTpsSampleTime = now;
                return;
            }

            float dt = now - lastTpsSampleTime;
            if (dt >= 0.25f)
            {
                int deltaTicks = Math.Max(0, curTicks - lastFrameTicksGame);
                RealWallTps = deltaTicks / dt;
                lastFrameTicksGame = curTicks;
                lastTpsSampleTime = now;
            }
        }
    }

    [HarmonyPatch(typeof(UIRoot_Play), "UIRootOnGUI")]
    public static class Patch_UIRoot_Play_UIRootOnGUI
    {
        public static void Postfix()
        {
            try
            {
                OmniThreadGUI.DrawTelemetryOverlay();
            }
            catch (Exception)
            {
            }
        }
    }
}
