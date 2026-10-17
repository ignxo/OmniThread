using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace OmniThreadUltimate
{
    [StaticConstructorOnStartup]
    public static class OmniThreadMod
    {
        public static bool EnableMultithreading = true;
        public static bool UncapMaxTimeSpeed = true;
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

                Application.runInBackground = true;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;

                AutoBenchmarkRunner.InitializeFromCommandLine();

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
