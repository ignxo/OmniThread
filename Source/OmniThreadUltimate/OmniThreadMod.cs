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
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                ModCompatibilityManager.Initialize(harmony);

                Log.Message(string.Format("[OmniThread Ultimate] Initialized successfully! MainThreadId={0}, LogicalCores={1}, IterationLevel={2}, Multithreading={3}, AutoTest={4}",
                    MainThreadSyncQueue.MainThreadId,
                    Environment.ProcessorCount,
                    IterationLevel,
                    EnableMultithreading,
                    AutoBenchmarkRunner.IsAutoTestMode));
            }
            catch (Exception ex)
            {
                Log.Error("[OmniThread Ultimate] Fatal initialization error: " + ex);
            }
        }
    }
}
