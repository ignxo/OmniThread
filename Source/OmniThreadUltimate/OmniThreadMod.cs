using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace OmniThreadUltimate
{
    public class OmniThreadModSettings : ModSettings
    {
        public bool EnableMultithreading = true;
        public bool UncapMaxTimeSpeed = true;
        public int MaxWorkerThreads = 0;
        public bool ShowOverlayOnStartup = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref EnableMultithreading, "EnableMultithreading", true);
            Scribe_Values.Look(ref UncapMaxTimeSpeed, "UncapMaxTimeSpeed", true);
            Scribe_Values.Look(ref MaxWorkerThreads, "MaxWorkerThreads", 0);
            Scribe_Values.Look(ref ShowOverlayOnStartup, "ShowOverlayOnStartup", false);
        }

        public void ApplyToRuntime()
        {
            if (!AutoBenchmarkRunner.IsAutoTestMode)
            {
                OmniThreadMod.EnableMultithreading = EnableMultithreading;
                OmniThreadMod.UncapMaxTimeSpeed = UncapMaxTimeSpeed;
                OmniThreadMod.MaxWorkerThreads = MaxWorkerThreads;
                OmniThreadGUI.ShowOverlay = ShowOverlayOnStartup;
            }
        }
    }

    public class OmniThreadModController : Mod
    {
        public static OmniThreadModSettings Settings;

        public OmniThreadModController(ModContentPack content) : base(content)
        {
            try
            {
                Settings = GetSettings<OmniThreadModSettings>();
                if (Settings != null)
                {
                    Settings.ApplyToRuntime();
                }
            }
            catch
            {
            }
        }

        public override string SettingsCategory()
        {
            return "OmniThread Ultimate";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (Settings == null)
            {
                return;
            }

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.CheckboxLabeled(
                "Enable Multi-Threaded Simulation Engine",
                ref Settings.EnableMultithreading,
                "Parallelizes TickList, Pawn.DoTick, NeedsTracker, and AttackTargetsCache across worker threads.");
            listing.Gap(6f);

            listing.CheckboxLabeled(
                "Uncap Max Simulation Speed (>>>)",
                ref Settings.UncapMaxTimeSpeed,
                "Removes the vanilla 360 TPS ceiling on Superfast/Ultrafast speeds while preserving Auto-Hz frame pacing.");
            listing.Gap(6f);

            listing.CheckboxLabeled(
                "Show Telemetry Overlay on Startup [F10]",
                ref Settings.ShowOverlayOnStartup,
                "Automatically opens the live TPS and worker thread monitor when loading a map (toggle anytime with F10).");
            listing.Gap(12f);

            int maxCores = Math.Max(4, Environment.ProcessorCount);
            string workersLabel = Settings.MaxWorkerThreads <= 0
                ? string.Format("Worker Thread Limit: Auto ({0} Logical Cores)", Environment.ProcessorCount)
                : string.Format("Worker Thread Limit: {0} Threads", Settings.MaxWorkerThreads);
            listing.Label(workersLabel);
            Settings.MaxWorkerThreads = Mathf.RoundToInt(listing.Slider(Settings.MaxWorkerThreads, 0f, maxCores));

            listing.End();

            OmniThreadMod.EnableMultithreading = Settings.EnableMultithreading;
            OmniThreadMod.UncapMaxTimeSpeed = Settings.UncapMaxTimeSpeed;
            OmniThreadMod.MaxWorkerThreads = Settings.MaxWorkerThreads;
        }
    }

    [StaticConstructorOnStartup]
    public static class OmniThreadMod
    {
        public static bool EnableMultithreading = true;
        public static bool UncapMaxTimeSpeed = true;
        public static int MaxWorkerThreads = 0;
        public static int IterationLevel = 5;

        static OmniThreadMod()
        {
            try
            {
                MainThreadSyncQueue.InitializeOnMainThread();

                int cores = Math.Max(4, Environment.ProcessorCount);
                int minWorker;
                int minIOC;
                ThreadPool.GetMinThreads(out minWorker, out minIOC);
                if (minWorker < cores * 2)
                {
                    ThreadPool.SetMinThreads(cores * 2, Math.Max(minIOC, cores));
                }

                AutoBenchmarkRunner.InitializeFromCommandLine();

                if (AutoBenchmarkRunner.IsAutoTestMode)
                {
                    ApplyAutoTestGraphicsSettings();
                }
                else if (OmniThreadModController.Settings != null)
                {
                    OmniThreadModController.Settings.ApplyToRuntime();
                }

                Harmony harmony = new Harmony("omnithread.ultimate.harmony");
                int patchedClasses = PatchAllResilient(harmony, Assembly.GetExecutingAssembly());
                ApplyCrossVersionFallbacks(harmony);
                ModCompatibilityManager.Initialize(harmony);

                Log.Message(string.Format("[OmniThread Ultimate] Initialized successfully! MainThreadId={0}, LogicalCores={1}, IterationLevel={2}, Multithreading={3}, UncapMaxSpeed={4}, PatchedClasses={5}, AutoTest={6}",
                    MainThreadSyncQueue.MainThreadId,
                    Environment.ProcessorCount,
                    IterationLevel,
                    EnableMultithreading,
                    UncapMaxTimeSpeed,
                    patchedClasses,
                    AutoBenchmarkRunner.IsAutoTestMode));
            }
            catch (Exception ex)
            {
                Log.Error("[OmniThread Ultimate] Fatal initialization error: " + ex);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyAutoTestGraphicsSettings()
        {
            try
            {
                Application.runInBackground = true;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
            }
            catch
            {
            }
        }

        private static bool IsBenchmarkOnlyPatchType(Type type)
        {
            if (type == null)
            {
                return false;
            }
            string name = type.Name;
            return name == "Patch_MainMenuDrawer_Init"
                || name == "Patch_MainMenuDrawer_MainMenuOnGUI"
                || name == "Patch_Root_Play_Update"
                || name == "Patch_TickManager_ForcePaused";
        }

        private static int PatchAllResilient(Harmony harmony, Assembly assembly)
        {
            int successCount = 0;
            Type[] types;
            try
            {
                types = AccessTools.GetTypesFromAssembly(assembly);
            }
            catch (ReflectionTypeLoadException rtle)
            {
                types = rtle.Types;
            }

            if (types == null)
            {
                return 0;
            }

            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == null)
                {
                    continue;
                }

                if (!AutoBenchmarkRunner.IsAutoTestMode && IsBenchmarkOnlyPatchType(type))
                {
                    continue;
                }

                try
                {
                    object[] attrs = type.GetCustomAttributes(typeof(HarmonyPatch), true);
                    if (attrs != null && attrs.Length > 0)
                    {
                        harmony.CreateClassProcessor(type).Patch();
                        successCount++;
                    }
                }
                catch
                {
                    // Gracefully skip version-specific patch classes when running on older RimWorld versions (1.3 / 1.4 / 1.5)
                }
            }
            return successCount;
        }

        private static void ApplyCrossVersionFallbacks(Harmony harmony)
        {
            try
            {
                // RimWorld 1.3 / 1.4 / 1.5 PathFinder.FindPath fallback (replaced by FindPathNow in 1.6)
                Type pfType = AccessTools.TypeByName("Verse.AI.PathFinder");
                MethodInfo enterLock = AccessTools.Method(typeof(ModCompatibilityManager), "EnterUnifiedModLock");
                MethodInfo exitLock = AccessTools.Method(typeof(ModCompatibilityManager), "ExitUnifiedModLock");
                if (pfType != null && enterLock != null && exitLock != null)
                {
                    MethodInfo[] pfMethods = pfType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < pfMethods.Length; i++)
                    {
                        MethodInfo m = pfMethods[i];
                        if (m != null && m.Name == "FindPath" && !m.IsAbstract)
                        {
                            try
                            {
                                harmony.Patch(m, new HarmonyMethod(enterLock), null, null, new HarmonyMethod(exitLock));
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }
}
