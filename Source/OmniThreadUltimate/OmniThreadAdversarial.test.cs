using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace OmniThreadUltimate.Tests
{
    public sealed class MockStatWorker : StatWorker
    {
        public int ComputeCalls;
        public float BaseReturnValue = 42.5f;

        public override float GetValueUnfinalized(StatRequest req, bool applyPostProcess = true)
        {
            Interlocked.Increment(ref ComputeCalls);
            return BaseReturnValue + (applyPostProcess ? 10f : 0f);
        }

        public override void FinalizeValue(StatRequest req, ref float val, bool applyPostProcess)
        {
            val += 1f;
        }
    }

    public sealed class MockModThing : Thing
    {
        public int TickCalls;
        public bool ThrowOnTick;
        public int LastTickThreadId;
        public bool TickedWhileInParallelSection;
        public int SleepMsOnTick;

        protected override void Tick()
        {
            LastTickThreadId = Thread.CurrentThread.ManagedThreadId;
            TickedWhileInParallelSection = MainThreadSyncQueue.InParallelSection;
            if (SleepMsOnTick > 0)
            {
                Thread.Sleep(SleepMsOnTick);
            }
            Interlocked.Increment(ref TickCalls);
            if (ThrowOnTick)
            {
                throw new InvalidOperationException("Simulated worker crash during DoTick()");
            }
        }
    }

    public sealed class ParallelWorkerThing : Thing
    {
        public int TickCalls;
        public bool ThrowOnTick;
        public int LastTickThreadId;
        public bool TickedWhileInParallelSection;
        public int SleepMsOnTick;

        protected override void Tick()
        {
            LastTickThreadId = Thread.CurrentThread.ManagedThreadId;
            TickedWhileInParallelSection = MainThreadSyncQueue.InParallelSection;
            if (SleepMsOnTick > 0)
            {
                Thread.Sleep(SleepMsOnTick);
            }
            Interlocked.Increment(ref TickCalls);
            if (ThrowOnTick)
            {
                throw new InvalidOperationException("Simulated worker crash during DoTick()");
            }
        }
    }

    public sealed class MinHashThing : Thing
    {
        public int TickCalls;

        public override int GetHashCode()
        {
            return int.MinValue;
        }

        protected override void Tick()
        {
            Interlocked.Increment(ref TickCalls);
        }
    }

    public static class OmniThreadAdversarialTestRunner
    {
        private static int _passed;
        private static int _failed;
        private static readonly List<string> _failures = new List<string>();
        private static bool _envInitialized;

        public static bool SuppressVerseLogPrefix()
        {
            return false;
        }

        public static void StubInternalCallsInAssembly(string inputDllPath, string outputDllPath, string searchDir)
        {
            Assembly cecilAsm = typeof(Harmony).Assembly;
            Type asmDefType = cecilAsm.GetType("Mono.Cecil.AssemblyDefinition");
            Type readerParamsType = cecilAsm.GetType("Mono.Cecil.ReaderParameters");
            Type resolverType = cecilAsm.GetType("Mono.Cecil.BaseAssemblyResolver");
            Type defaultResolverType = cecilAsm.GetType("Mono.Cecil.DefaultAssemblyResolver");
            Type methodBodyType = cecilAsm.GetType("Mono.Cecil.Cil.MethodBody");
            Type varDefType = cecilAsm.GetType("Mono.Cecil.Cil.VariableDefinition");
            Type opCodesType = cecilAsm.GetType("Mono.Cecil.Cil.OpCodes");
            Type opCodeType = cecilAsm.GetType("Mono.Cecil.Cil.OpCode");
            Type typeRefType = cecilAsm.GetType("Mono.Cecil.TypeReference");
            Type paramDefType = cecilAsm.GetType("Mono.Cecil.ParameterDefinition");

            object resolver = Activator.CreateInstance(defaultResolverType);
            resolverType.GetMethod("AddSearchDirectory", new Type[] { typeof(string) }).Invoke(resolver, new object[] { searchDir });

            object readerParams = Activator.CreateInstance(readerParamsType);
            readerParamsType.GetProperty("AssemblyResolver").SetValue(readerParams, resolver, null);

            MethodInfo readAsm = asmDefType.GetMethod("ReadAssembly", new Type[] { typeof(string), readerParamsType });
            object asmDef = readAsm.Invoke(null, new object[] { inputDllPath, readerParams });

            object mainModule = asmDefType.GetProperty("MainModule").GetValue(asmDef, null);
            System.Collections.IEnumerable allTypes = (System.Collections.IEnumerable)mainModule.GetType().GetMethod("GetTypes", Type.EmptyTypes).Invoke(mainModule, null);

            object opRet = opCodesType.GetField("Ret").GetValue(null);
            object opLdnull = opCodesType.GetField("Ldnull").GetValue(null);
            object opLdloca = opCodesType.GetField("Ldloca").GetValue(null);
            object opLdloc = opCodesType.GetField("Ldloc").GetValue(null);
            object opInitobj = opCodesType.GetField("Initobj").GetValue(null);
            object opLdarg = opCodesType.GetField("Ldarg").GetValue(null);
            object opStobj = opCodesType.GetField("Stobj").GetValue(null);

            foreach (object typeDef in allTypes)
            {
                System.Collections.IEnumerable methods = (System.Collections.IEnumerable)typeDef.GetType().GetProperty("Methods").GetValue(typeDef, null);
                foreach (object m in methods)
                {
                    Type mType = m.GetType();
                    bool isInternalCall = (bool)mType.GetProperty("IsInternalCall").GetValue(m, null);
                    bool isPInvoke = (bool)mType.GetProperty("IsPInvokeImpl").GetValue(m, null);
                    if (isInternalCall || isPInvoke)
                    {
                        mType.GetProperty("IsInternalCall").SetValue(m, false, null);
                        mType.GetProperty("IsPInvokeImpl").SetValue(m, false, null);
                        mType.GetProperty("IsIL").SetValue(m, true, null);
                        mType.GetProperty("IsManaged").SetValue(m, true, null);

                        object body = Activator.CreateInstance(methodBodyType, new object[] { m });
                        methodBodyType.GetProperty("InitLocals").SetValue(body, true, null);
                        object vars = methodBodyType.GetProperty("Variables").GetValue(body, null);
                        MethodInfo addVar = vars.GetType().GetMethod("Add", new Type[] { varDefType });

                        object il = methodBodyType.GetMethod("GetILProcessor").Invoke(body, null);
                        Type ilType = il.GetType();
                        MethodInfo emitOp = ilType.GetMethod("Emit", new Type[] { opCodeType });
                        MethodInfo emitVar = ilType.GetMethod("Emit", new Type[] { opCodeType, varDefType });
                        MethodInfo emitType = ilType.GetMethod("Emit", new Type[] { opCodeType, typeRefType });
                        MethodInfo emitParam = ilType.GetMethod("Emit", new Type[] { opCodeType, paramDefType });

                        System.Collections.IEnumerable parameters = (System.Collections.IEnumerable)mType.GetProperty("Parameters").GetValue(m, null);
                        foreach (object p in parameters)
                        {
                            Type pType = p.GetType();
                            object pTypeRef = pType.GetProperty("ParameterType").GetValue(p, null);
                            bool isByRef = (bool)pTypeRef.GetType().GetProperty("IsByReference").GetValue(pTypeRef, null);
                            if (isByRef)
                            {
                                object elemType = pTypeRef.GetType().GetMethod("GetElementType").Invoke(pTypeRef, null);
                                bool elemVal = (bool)elemType.GetType().GetProperty("IsValueType").GetValue(elemType, null);
                                bool elemGen = (bool)elemType.GetType().GetProperty("IsGenericParameter").GetValue(elemType, null);
                                if (elemVal || elemGen)
                                {
                                    object loc = Activator.CreateInstance(varDefType, new object[] { elemType });
                                    addVar.Invoke(vars, new object[] { loc });
                                    emitParam.Invoke(il, new object[] { opLdarg, p });
                                    emitVar.Invoke(il, new object[] { opLdloca, loc });
                                    emitType.Invoke(il, new object[] { opInitobj, elemType });
                                    emitVar.Invoke(il, new object[] { opLdloc, loc });
                                    emitType.Invoke(il, new object[] { opStobj, elemType });
                                }
                            }
                        }

                        object retType = mType.GetProperty("ReturnType").GetValue(m, null);
                        string retFullName = (string)retType.GetType().GetProperty("FullName").GetValue(retType, null);
                        if (retFullName != "System.Void")
                        {
                            bool isVal = (bool)retType.GetType().GetProperty("IsValueType").GetValue(retType, null);
                            bool isGen = (bool)retType.GetType().GetProperty("IsGenericParameter").GetValue(retType, null);
                            if (isVal || isGen)
                            {
                                object retLoc = Activator.CreateInstance(varDefType, new object[] { retType });
                                addVar.Invoke(vars, new object[] { retLoc });
                                emitVar.Invoke(il, new object[] { opLdloca, retLoc });
                                emitType.Invoke(il, new object[] { opInitobj, retType });
                                emitVar.Invoke(il, new object[] { opLdloc, retLoc });
                            }
                            else
                            {
                                emitOp.Invoke(il, new object[] { opLdnull });
                            }
                        }
                        emitOp.Invoke(il, new object[] { opRet });
                        mType.GetProperty("Body").SetValue(m, body, null);
                    }
                }
            }

            asmDefType.GetMethod("Write", new Type[] { typeof(string) }).Invoke(asmDef, new object[] { outputDllPath });
            if (asmDef is IDisposable)
            {
                ((IDisposable)asmDef).Dispose();
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void InitOmniThreadModAfterLogPatched()
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(OmniThreadMod).TypeHandle);
            if (ModCompatibilityManager.BaseMainThreadTypes.Count == 0)
            {
                ModCompatibilityManager.Initialize(new Harmony("omnithread.ultimate.test"));
                ModCompatibilityManager.EnsureLateCompatibilityPatchesApplied();
            }
            MainThreadSyncQueue.InitializeOnMainThread();
            OmniThreadMod.EnableMultithreading = true;
            OmniThreadMod.IterationLevel = 5;
        }

        private static void EnsureStandaloneEnvironment()
        {
            if (_envInitialized)
            {
                return;
            }
            _envInitialized = true;

            SetupMockGameAndMap(1000);
            MainThreadSyncQueue.InitializeOnMainThread();
        }

        private static Map SetupMockGameAndMap(int ticksGame)
        {
            Game mockGame = CreateMock<Game>();
            TickManager mockTm = CreateMock<TickManager>();
            SetField(mockTm, "ticksGameInt", ticksGame);
            SetField(mockTm, "curTimeSpeed", TimeSpeed.Normal);
            SetField(mockGame, "tickManager", mockTm);

            Map mockMap = CreateMock<Map>();
            mockMap.uniqueID = 1;
            List<Map> maps = new List<Map> { mockMap };
            SetField(mockGame, "maps", maps);
            SetStaticField(typeof(Current), "gameInt", mockGame);
            return mockMap;
        }

        private static T CreateMock<T>() where T : class
        {
            return (T)FormatterServices.GetUninitializedObject(typeof(T));
        }

        private static void SetField(object target, string fieldName, object value)
        {
            Type t = target.GetType();
            while (t != null)
            {
                FieldInfo fi = t.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fi != null)
                {
                    fi.SetValue(target, value);
                    return;
                }
                t = t.BaseType;
            }
            throw new InvalidOperationException("Field not found: " + fieldName);
        }

        private static void SetStaticField(Type t, string fieldName, object value)
        {
            FieldInfo fi = t.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi != null)
            {
                fi.SetValue(null, value);
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }

        private static void RunTestWithWatchdog(string name, Action testAction, int timeoutMs = 4000)
        {
            try
            {
                Exception caught = null;
                Thread runner = new Thread(() =>
                {
                    try
                    {
                        testAction();
                    }
                    catch (Exception ex)
                    {
                        caught = ex;
                    }
                });
                runner.IsBackground = true;
                runner.Start();
                bool finished = runner.Join(timeoutMs);
                if (!finished)
                {
                    try { runner.Abort(); } catch { }
                    _failed++;
                    string msg = string.Format("[TIMEOUT] {0} exceeded {1}ms watchdog!", name, timeoutMs);
                    _failures.Add(msg);
                    Console.WriteLine(msg);
                    return;
                }
                if (caught != null)
                {
                    _failed++;
                    string msg = string.Format("[FAIL] {0}: {1}", name, caught);
                    _failures.Add(msg);
                    Console.WriteLine(msg);
                    return;
                }
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception outer)
            {
                _failed++;
                string msg = string.Format("[FAIL] {0} (Runner): {1}", name, outer);
                _failures.Add(msg);
                Console.WriteLine(msg);
            }
        }

        public static int Main(string[] args)
        {
            if (args != null && args.Length == 4 && args[0] == "--patch-ecalls")
            {
                StubInternalCallsInAssembly(args[1], args[2], args[3]);
                return 0;
            }

            try
            {
                Harmony h = new Harmony("omnithread.testrunner.logsuppress");
                MethodInfo prefix = typeof(OmniThreadAdversarialTestRunner).GetMethod("SuppressVerseLogPrefix", BindingFlags.Static | BindingFlags.Public);
                HarmonyMethod hm = new HarmonyMethod(prefix);
                MethodInfo mMsg = typeof(Log).GetMethod("Message", new Type[] { typeof(string) });
                MethodInfo mWarn = typeof(Log).GetMethod("Warning", new Type[] { typeof(string) });
                MethodInfo mErr = typeof(Log).GetMethod("Error", new Type[] { typeof(string) });
                if (mMsg != null) h.Patch(mMsg, hm);
                if (mWarn != null) h.Patch(mWarn, hm);
                if (mErr != null) h.Patch(mErr, hm);
            }
            catch
            {
            }

            InitOmniThreadModAfterLogPatched();
            return RunAll();
        }

        public static int RunAll()
        {
            _passed = 0;
            _failed = 0;
            _failures.Clear();

            EnsureStandaloneEnvironment();

            RunTestWithWatchdog("1. MainThreadSyncQueue_BoundaryAndNullSafety", Test_MainThreadSyncQueue_BoundaryAndNullSafety);
            RunTestWithWatchdog("2. MainThreadSyncQueue_WorkerThreadCircuitBreaker1024", Test_MainThreadSyncQueue_WorkerThreadCircuitBreaker1024);
            RunTestWithWatchdog("3. MainThreadSyncQueue_GlobalCircuitBreaker4096", Test_MainThreadSyncQueue_GlobalCircuitBreaker4096);
            RunTestWithWatchdog("4. MainThreadSyncQueue_FlushExceptionIsolationAndNonMainThread", Test_MainThreadSyncQueue_FlushExceptionIsolationAndNonMainThread);
            RunTestWithWatchdog("5. MainThreadSyncQueue_RecordExceptionsAndLogErrorsEdgeCases", Test_MainThreadSyncQueue_RecordExceptionsAndLogErrorsEdgeCases);
            RunTestWithWatchdog("6. SpatialPartitioner_AdversarialCoordinatesAndStates", Test_SpatialPartitioner_AdversarialCoordinatesAndStates);
            RunTestWithWatchdog("7. SpatialPartitioner_PoolReuseAcrossIterations", Test_SpatialPartitioner_PoolReuseAcrossIterations);
            RunTestWithWatchdog("8. ModCompatibilityManager_FaultedThingIds_NegativeZeroHugeAndConcurrent", Test_ModCompatibilityManager_FaultedThingIds_NegativeZeroHugeAndConcurrent);
            RunTestWithWatchdog("9. ModCompatibilityManager_HandleWorkerThingException_DemotesThing", Test_ModCompatibilityManager_HandleWorkerThingException_DemotesThing);
            RunTestWithWatchdog("10. ModCompatibilityManager_UnifiedLocksAndPrefixes", Test_ModCompatibilityManager_UnifiedLocksAndPrefixes);
            RunTestWithWatchdog("11. ParallelTickEngine_OptionsAndTickIntervals_Boundaries", Test_ParallelTickEngine_OptionsAndTickIntervals_Boundaries);
            RunTestWithWatchdog("12. ParallelTickEngine_TelemetryAndDiagnostics_Concurrency", Test_ParallelTickEngine_TelemetryAndDiagnostics_Concurrency);
            RunTestWithWatchdog("13. ThreadLocalRandom_ConcurrencyAndStateStackUnderflow", Test_ThreadLocalRandom_ConcurrencyAndStateStackUnderflow);
            RunTestWithWatchdog("14. ThreadSafeMapPawns_ConcurrencyAndInvalidation", Test_ThreadSafeMapPawns_ConcurrencyAndInvalidation);
            RunTestWithWatchdog("15. ThreadLocalCapacityCache_AdversarialInputsAndConcurrency", Test_ThreadLocalCapacityCache_AdversarialInputsAndConcurrency);
            RunTestWithWatchdog("16. ThreadLocalStatCache_AdversarialInputsAndConcurrency", Test_ThreadLocalStatCache_AdversarialInputsAndConcurrency);
            RunTestWithWatchdog("17. HarmonyPatches_LoopGuardsAndDeduplication", Test_HarmonyPatches_LoopGuardsAndDeduplication);
            RunTestWithWatchdog("18. HarmonyPatches_ReentrantLocksAndFinalizers", Test_HarmonyPatches_ReentrantLocksAndFinalizers);
            RunTestWithWatchdog("19. AutoBenchmarkRunner_EdgeCasesAndSpeedPatches", Test_AutoBenchmarkRunner_EdgeCasesAndSpeedPatches);
            RunTestWithWatchdog("20. ParallelTickEngine_ExecuteTickList_AllIterationLevelsAndFaultIsolation", Test_ParallelTickEngine_ExecuteTickList_AllIterationLevelsAndFaultIsolation);
            RunTestWithWatchdog("21. Bug1_SpatialPartitioner_NoDuplicateSectorKeysInColorBatch", Test_Bug1_SpatialPartitioner_NoDuplicateSectorKeysInColorBatch);
            RunTestWithWatchdog("22. Bug2_MainThreadRouting_SpawnedFaulted_Skyfaller_And_Iter1", Test_Bug2_MainThreadRouting_SpawnedFaulted_Skyfaller_And_Iter1);
            RunTestWithWatchdog("23. Bug3_Iteration5_UnspawnedThingTicksOnceOnMainThread", Test_Bug3_Iteration5_UnspawnedThingTicksOnceOnMainThread);
            RunTestWithWatchdog("24. Bug4_ParallelTickEngine_GetBucket_IntMinValueHashCode", Test_Bug4_ParallelTickEngine_GetBucket_IntMinValueHashCode);
            RunTestWithWatchdog("25. Bug5_NullSafety_PartitionThingsAndShouldTickOnMainThread", Test_Bug5_NullSafety_PartitionThingsAndShouldTickOnMainThread);
            RunTestWithWatchdog("26. Bug6_ParallelTickEngine_TelemetryCacheLinePaddingAndSync", Test_Bug6_ParallelTickEngine_TelemetryCacheLinePaddingAndSync);

            Console.WriteLine(string.Format("=== SUMMARY: Passed={0}, Failed={1} ===", _passed, _failed));
            if (_failed > 0)
            {
                foreach (string f in _failures)
                {
                    Console.WriteLine(f);
                }
            }
            return _failed;
        }

        private static void Test_MainThreadSyncQueue_BoundaryAndNullSafety()
        {
            MainThreadSyncQueue.InitializeOnMainThread();
            Assert(MainThreadSyncQueue.IsMainThread, "Current thread should be registered as MainThread");
            MainThreadSyncQueue.Flush();

            long before = MainThreadSyncQueue.TotalEnqueuedActions;
            MainThreadSyncQueue.Enqueue(null);
            Assert(MainThreadSyncQueue.TotalEnqueuedActions == before, "Enqueue(null) must not increment TotalEnqueuedActions");

            Sustainer dummySustainer = MainThreadSyncQueue.GetDummySustainer();
            MainThreadSyncQueue.SetSustainerMaintainTickSafe(null, 123);
            MainThreadSyncQueue.SetSustainerMaintainTickSafe(dummySustainer, 123);

            Sustainer realMockSustainer = CreateMock<Sustainer>();
            MainThreadSyncQueue.SetSustainerMaintainTickSafe(realMockSustainer, 777);
            FieldInfo fi = typeof(Sustainer).GetField("lastMaintainTick", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fi != null)
            {
                Assert((int)fi.GetValue(realMockSustainer) == 777, "SetSustainerMaintainTickSafe should update lastMaintainTick on non-dummy sustainer");
            }
        }

        private static void Test_MainThreadSyncQueue_WorkerThreadCircuitBreaker1024()
        {
            MainThreadSyncQueue.InitializeOnMainThread();
            MainThreadSyncQueue.Flush();
            MainThreadSyncQueue.InParallelSection = true;

            try
            {
                bool threwCircuitBreaker = false;
                Thread worker = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < 1030; i++)
                        {
                            MainThreadSyncQueue.Enqueue(() => { });
                        }
                    }
                    catch (InvalidOperationException ex)
                    {
                        if (ex.Message.Contains("1024"))
                        {
                            threwCircuitBreaker = true;
                        }
                    }
                });
                worker.Start();
                worker.Join();

                Assert(threwCircuitBreaker, "Worker thread enqueuing >1024 actions must trip the 1024 circuit breaker");
            }
            finally
            {
                MainThreadSyncQueue.InParallelSection = false;
                MainThreadSyncQueue.Flush();
            }
        }

        private static void Test_MainThreadSyncQueue_GlobalCircuitBreaker4096()
        {
            MainThreadSyncQueue.InitializeOnMainThread();
            MainThreadSyncQueue.Flush();
            MainThreadSyncQueue.InParallelSection = true;

            try
            {
                int trippedCount = 0;
                Thread[] workers = new Thread[5];
                for (int w = 0; w < 5; w++)
                {
                    workers[w] = new Thread(() =>
                    {
                        try
                        {
                            for (int i = 0; i < 900; i++)
                            {
                                MainThreadSyncQueue.Enqueue(() => { });
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            if (ex.Message.Contains("4096"))
                            {
                                Interlocked.Increment(ref trippedCount);
                            }
                        }
                    });
                    workers[w].Start();
                }
                for (int w = 0; w < 5; w++)
                {
                    workers[w].Join();
                }

                Assert(trippedCount > 0, "5 workers enqueuing 900 actions each (4500 total) must trip the 4096 global circuit breaker");
            }
            finally
            {
                MainThreadSyncQueue.InParallelSection = false;
                MainThreadSyncQueue.Flush();
            }
        }

        private static void Test_MainThreadSyncQueue_FlushExceptionIsolationAndNonMainThread()
        {
            MainThreadSyncQueue.InitializeOnMainThread();
            MainThreadSyncQueue.Flush();

            int executed = 0;
            MainThreadSyncQueue.Enqueue(() => { Interlocked.Increment(ref executed); });
            MainThreadSyncQueue.Enqueue(() => { throw new InvalidOperationException("Simulated callback failure"); });
            MainThreadSyncQueue.Enqueue(() => { Interlocked.Increment(ref executed); });

            // Attempt Flush from a worker thread -> must be a no-op
            Thread worker = new Thread(() =>
            {
                MainThreadSyncQueue.Flush();
            });
            worker.Start();
            worker.Join();
            Assert(executed == 0, "Flush from non-main thread must not execute queued actions");

            long exceptionsBefore = MainThreadSyncQueue.CaughtConcurrencyExceptions;
            MainThreadSyncQueue.Flush();
            Assert(executed == 2, "Flush on main thread must execute remaining actions even when one throws");
            Assert(MainThreadSyncQueue.CaughtConcurrencyExceptions == exceptionsBefore + 1, "Throwing action in Flush must be recorded via RecordWorkerException");
        }

        private static void Test_MainThreadSyncQueue_RecordExceptionsAndLogErrorsEdgeCases()
        {
            string discard;
            while (MainThreadSyncQueue.RecentExceptions.TryDequeue(out discard)) { }

            MainThreadSyncQueue.RecordWorkerException(null, null);
            MainThreadSyncQueue.RecordWorkerException("TestContext", new ArgumentNullException("badParam"));

            // Filtered benign log errors should not be added to RecentExceptions
            int countBeforeFilter = MainThreadSyncQueue.RecentExceptions.Count;
            MainThreadSyncQueue.RecordWorkerLogError(null);
            MainThreadSyncQueue.RecordWorkerLogError("");
            MainThreadSyncQueue.RecordWorkerLogError("OmniThread loop guard: test");
            MainThreadSyncQueue.RecordWorkerLogError("Can't start LayDown toil because pawn is not spawned");
            MainThreadSyncQueue.RecordWorkerLogError("Could not resolve symbol xyz");
            Assert(MainThreadSyncQueue.RecentExceptions.Count == countBeforeFilter, "Filtered/empty log errors must not populate RecentExceptions");

            // Cap at 50 entries
            for (int i = 0; i < 100; i++)
            {
                MainThreadSyncQueue.RecordWorkerLogError("Adversarial Error #" + i);
            }
            Assert(MainThreadSyncQueue.RecentExceptions.Count <= 50, "RecentExceptions must be bounded at 50 entries");
        }

        private static void Test_SpatialPartitioner_AdversarialCoordinatesAndStates()
        {
            SetupMockGameAndMap(1000);
            FieldInfo mapIndexOrStateField = typeof(Thing).GetField("mapIndexOrState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo idField = typeof(Thing).GetField("thingIDNumber", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo posField = typeof(Thing).GetField("positionInt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            Thing unspawned = CreateMock<Thing>();
            mapIndexOrStateField.SetValue(unspawned, (sbyte)-1);
            idField.SetValue(unspawned, 10);

            Thing destroyed = CreateMock<Thing>();
            mapIndexOrStateField.SetValue(destroyed, (sbyte)-2); // Destroyed = -2 in RimWorld
            idField.SetValue(destroyed, 11);

            // Spawned faultedThing (mapIndexOrState = 0, Map assigned) -> must route to mainThreadThings, NOT batches[color]
            Thing faultedThing = CreateMock<Thing>();
            mapIndexOrStateField.SetValue(faultedThing, (sbyte)0);
            idField.SetValue(faultedThing, 12);
            posField.SetValue(faultedThing, new IntVec3(2, 0, 2));
            ModCompatibilityManager.MarkThingFaultedForMainThread(faultedThing);

            // Spawned Skyfaller (ShouldTickOnMainThread == true) -> must route to mainThreadThings, NOT batches[color]
            Skyfaller spawnedSkyfaller = CreateMock<Skyfaller>();
            mapIndexOrStateField.SetValue(spawnedSkyfaller, (sbyte)0);
            idField.SetValue(spawnedSkyfaller, 16);
            posField.SetValue(spawnedSkyfaller, new IntVec3(3, 0, 3));

            // Spawned things with adversarial coordinates (negative, zero, extreme)
            Thing spawnedNeg = CreateMock<Thing>();
            mapIndexOrStateField.SetValue(spawnedNeg, (sbyte)0);
            idField.SetValue(spawnedNeg, 13);
            posField.SetValue(spawnedNeg, new IntVec3(int.MinValue, 0, -99999));

            Thing spawnedZero = CreateMock<Thing>();
            mapIndexOrStateField.SetValue(spawnedZero, (sbyte)0);
            idField.SetValue(spawnedZero, 14);
            posField.SetValue(spawnedZero, new IntVec3(0, 0, 0));

            Thing spawnedMax = CreateMock<Thing>();
            mapIndexOrStateField.SetValue(spawnedMax, (sbyte)0);
            idField.SetValue(spawnedMax, 15);
            posField.SetValue(spawnedMax, new IntVec3(int.MaxValue, 0, int.MaxValue));

            List<Thing> source = new List<Thing>
            {
                null,
                unspawned,
                destroyed,
                faultedThing,
                spawnedSkyfaller,
                spawnedNeg,
                spawnedZero,
                spawnedMax
            };

            // Add 12 things in the exact same sector (1,0,1)
            for (int k = 0; k < 12; k++)
            {
                Thing clusterThing = CreateMock<Thing>();
                mapIndexOrStateField.SetValue(clusterThing, (sbyte)0);
                idField.SetValue(clusterThing, 100 + k);
                posField.SetValue(clusterThing, new IntVec3(1, 0, 1));
                source.Add(clusterThing);
            }

            List<Thing> mainThreadThings;
            List<SpatialSectorBatch>[] batches = SpatialPartitioner.PartitionThings(source, out mainThreadThings);
            Assert(batches != null && batches.Length == 4, "Batches array must have 4 color groups");
            Assert(mainThreadThings != null, "mainThreadThings must not be null");
            Assert(!ContainsReference(mainThreadThings, null), "Null Thing must be ignored");
            Assert(!ContainsReference(mainThreadThings, destroyed), "Destroyed Thing must be ignored");
            Assert(ContainsReference(mainThreadThings, unspawned), "Unspawned Thing must be routed to mainThreadThings");
            Assert(ContainsReference(mainThreadThings, faultedThing), "Spawned faultedThing must be routed to mainThreadThings, not worker batches");
            Assert(ContainsReference(mainThreadThings, spawnedSkyfaller), "Spawned Skyfaller (ShouldTickOnMainThread==true) must be routed to mainThreadThings, not worker batches");

            int totalBatched = 0;
            for (int c = 0; c < 4; c++)
            {
                HashSet<int> seenSectorKeys = new HashSet<int>();
                for (int b = 0; b < batches[c].Count; b++)
                {
                    SpatialSectorBatch batch = batches[c][b];
                    Assert(!ContainsReference(batch.Things, faultedThing), "Spawned faultedThing must NEVER appear in color batches");
                    Assert(!ContainsReference(batch.Things, spawnedSkyfaller), "Spawned Skyfaller must NEVER appear in color batches");
                    Assert(seenSectorKeys.Add(batch.SectorKey),
                        string.Format("Duplicate SectorKey {0} in batches[{1}] — same 16x16 sector split into multiple parallel batches!", batch.SectorKey, c));
                    totalBatched += batch.Things.Count;
                }
            }
            Assert(totalBatched == 15, "All 15 valid parallel-safe spawned things (including negative/zero/max coordinates) must be partitioned into color batches");
        }

        private static bool ContainsReference<T>(List<T> list, T target) where T : class
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], target))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Test_SpatialPartitioner_PoolReuseAcrossIterations()
        {
            List<Thing> emptySource = new List<Thing>();
            for (int iter = 1; iter <= 5; iter++)
            {
                OmniThreadMod.IterationLevel = iter;
                List<Thing> mainThread;
                List<SpatialSectorBatch>[] batches = SpatialPartitioner.PartitionThings(emptySource, out mainThread);
                Assert(batches.Length == 4 && batches[0].Count == 0 && batches[1].Count == 0 && batches[2].Count == 0 && batches[3].Count == 0, "Empty source must yield 0 non-empty batches at iteration " + iter);
                Assert(mainThread.Count == 0, "Empty source must yield 0 mainThreadThings at iteration " + iter);
            }
            OmniThreadMod.IterationLevel = 5;
        }

        private static void Test_ModCompatibilityManager_FaultedThingIds_NegativeZeroHugeAndConcurrent()
        {
            FieldInfo idField = typeof(Thing).GetField("thingIDNumber", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            ModCompatibilityManager.MarkThingFaultedForMainThread(null);

            Thing negThing = CreateMock<Thing>();
            idField.SetValue(negThing, -500);
            ModCompatibilityManager.MarkThingFaultedForMainThread(negThing);
            Assert(!ModCompatibilityManager.ShouldTickOnMainThread(negThing, typeof(Thing)), "Negative thingIDNumber must not be marked faulted");

            Thing zeroThing = CreateMock<Thing>();
            idField.SetValue(zeroThing, 0);
            ModCompatibilityManager.MarkThingFaultedForMainThread(zeroThing);
            Assert(ModCompatibilityManager.ShouldTickOnMainThread(zeroThing, typeof(Thing)), "Zero thingIDNumber should be marked faulted when requested");

            // Concurrent resize stress test with large IDs up to 500,000
            Parallel.For(0, 32, i =>
            {
                Thing t = CreateMock<Thing>();
                int id = (i + 1) * 15000;
                idField.SetValue(t, id);
                ModCompatibilityManager.MarkThingFaultedForMainThread(t);
                Assert(ModCompatibilityManager.ShouldTickOnMainThread(t, typeof(Thing)), "Concurrent faulted Thing ID " + id + " must be readable immediately");
            });

            Assert(ModCompatibilityManager.FindModType(null) == null, "FindModType(null) must return null");
            Assert(ModCompatibilityManager.FindModType("") == null, "FindModType(\"\") must return null");
            Assert(ModCompatibilityManager.FindModType("NonExistent.Namespace.TypeXYZ") == null, "FindModType for nonexistent type must return null");
        }

        private static void Test_ModCompatibilityManager_HandleWorkerThingException_DemotesThing()
        {
            MainThreadSyncQueue.InitializeOnMainThread();
            MainThreadSyncQueue.Flush();

            Assert(!ModCompatibilityManager.HandleWorkerThingException(null, "NullThing", new Exception("test")), "HandleWorkerThingException(null) must return false");

            FieldInfo idField = typeof(Thing).GetField("thingIDNumber", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Thing problematic = CreateMock<Thing>();
            idField.SetValue(problematic, 42424);

            Assert(!ModCompatibilityManager.ShouldTickOnMainThread(problematic, typeof(Thing)), "Initially should not require main thread");

            // Case 1: Vanilla Thing with generic exception -> demotes the Thing instance (FaultedThingIds), returns false (does not demote entire vanilla Thing type)
            bool vanillaTypeDemoted = ModCompatibilityManager.HandleWorkerThingException(problematic, "TestContext", new NullReferenceException("Simulated crash"));
            Assert(!vanillaTypeDemoted, "HandleWorkerThingException must return false for vanilla Thing type");
            Assert(ModCompatibilityManager.ShouldTickOnMainThread(problematic, typeof(Thing)), "Faulted vanilla Thing instance must be demoted to main thread after worker exception");

            // Case 2: Third-party mod Thing subclass with Unity main-thread error -> demotes both instance and custom type, returns true
            MockModThing modThing1 = CreateMock<MockModThing>();
            idField.SetValue(modThing1, 42425);
            MockModThing modThing2 = CreateMock<MockModThing>();
            idField.SetValue(modThing2, 42426);

            bool modTypeDemoted = ModCompatibilityManager.HandleWorkerThingException(modThing1, "ModContext", new InvalidOperationException("can only be called from the main thread"));
            Assert(modTypeDemoted, "HandleWorkerThingException must return true when demoting custom mod Thing subclass on main-thread exception");
            Assert(ModCompatibilityManager.ShouldTickOnMainThread(modThing1, typeof(MockModThing)), "Faulted mod Thing instance must require main thread");
            Assert(ModCompatibilityManager.ShouldTickOnMainThread(modThing2, typeof(MockModThing)), "Second instance of demoted MockModThing type must also require main thread");
            Assert(ModCompatibilityManager.AutoSafeModFallbackTypesCount >= 1, "AutoSafeModFallbackTypesCount must be >= 1 after mod type demotion");

            bool removedDecision;
            ModCompatibilityManager.TypeMainThreadDecisionCache.TryRemove(typeof(MockModThing), out removedDecision);
        }

        private static void Test_ModCompatibilityManager_UnifiedLocksAndPrefixes()
        {
            OmniThreadMod.EnableMultithreading = true;
            MainThreadSyncQueue.InParallelSection = true;
            try
            {
                bool state = false;
                ModCompatibilityManager.EnterUnifiedModLock(ref state);
                Assert(state, "EnterUnifiedModLock must acquire lock when in parallel section");
                ModCompatibilityManager.ExitUnifiedModLock(null, state);

                bool leafState = false;
                ModCompatibilityManager.EnterLeafModLock(ref leafState);
                Assert(leafState, "EnterLeafModLock must acquire lock when in parallel section");
                ModCompatibilityManager.ExitLeafModLock(null, leafState);

                Assert(ModCompatibilityManager.SkipWhenInParallelPrefix() == false, "SkipWhenInParallelPrefix must return false in parallel section");
                bool boolResult = false;
                Assert(ModCompatibilityManager.SkipBoolPrefixWhenInParallel(ref boolResult) == false && boolResult == true, "SkipBoolPrefixWhenInParallel must skip original and set __result = true");

                float caiStatRes = -1f;
                Assert(ModCompatibilityManager.BypassCombatAIStatCacheInParallel(null, null, true, ref caiStatRes) == false && caiStatRes == 0f, "BypassCombatAIStatCacheInParallel(null, null) must return false and set __result = 0f");
            }
            finally
            {
                MainThreadSyncQueue.InParallelSection = false;
            }
        }

        private static void Test_ParallelTickEngine_OptionsAndTickIntervals_Boundaries()
        {
            for (int level = -10; level <= 10; level++)
            {
                ParallelOptions opts = ParallelTickEngine.GetParallelOptionsForIteration(level);
                Assert(opts != null && opts.MaxDegreeOfParallelism >= 1, "GetParallelOptionsForIteration must always return valid ParallelOptions with MaxDegreeOfParallelism >= 1");
            }

            Assert(ParallelTickEngine.GetTickIntervalFor(TickerType.Normal) == 1, "GetTickIntervalFor(Normal) must return 1");
            Assert(ParallelTickEngine.GetTickIntervalFor(TickerType.Rare) == 250, "GetTickIntervalFor(Rare) must return 250");
            Assert(ParallelTickEngine.GetTickIntervalFor(TickerType.Long) == 2000, "GetTickIntervalFor(Long) must return 2000");
            Assert(ParallelTickEngine.GetTickIntervalFor(unchecked((TickerType)999)) == 1, "GetTickIntervalFor(invalid enum) must fallback to 1");
        }

        private static void Test_ParallelTickEngine_TelemetryAndDiagnostics_Concurrency()
        {
            OmniThreadMod.IterationLevel = 5;
            ParallelTickEngine.ResetThreadWorkCounts();
            ParallelTickEngine.BeginTickMeasurement();
            Parallel.For(0, 16, i =>
            {
                for (int j = 0; j < 50; j++)
                {
                    ParallelTickEngine.RecordThreadWork();
                }
            });
            ParallelTickEngine.EndTickMeasurement();
            ParallelTickEngine.SyncThreadWorkDictionary();

            Assert(ParallelTickEngine.ActiveWorkerThreadsLastTick >= 1, "ActiveWorkerThreadsLastTick must be >= 1 after parallel work");
            Assert(ParallelTickEngine.ThreadWorkCounts.Count > 0, "SyncThreadWorkDictionary must populate ThreadWorkCounts");
        }

        private static void Test_ThreadLocalRandom_ConcurrencyAndStateStackUnderflow()
        {
            Parallel.For(0, 16, i =>
            {
                ThreadLocalRandom.SetSeed(0); // Seed 0 edge case -> should fallback to 1337u
                uint u0 = ThreadLocalRandom.NextUInt();
                Assert(u0 != 0, "Seed 0 must produce non-zero NextUInt()");

                ThreadLocalRandom.SetSeed(12345 + i);
                float val1 = ThreadLocalRandom.NextValue();
                Assert(val1 >= 0f && val1 <= 1f, "Random NextValue() must be in [0, 1]");
                int rInt = ThreadLocalRandom.NextInt();

                // Save sequence position via PushState, overwrite seed, then PopState
                ThreadLocalRandom.PushState();
                uint expectedNext = ThreadLocalRandom.NextUInt();
                ThreadLocalRandom.PopState();

                ThreadLocalRandom.PushState(999999);
                ThreadLocalRandom.NextUInt();
                ThreadLocalRandom.PopState();
                ThreadLocalRandom.PopState(); // Intentional extra PopState (underflow test)

                uint actualNext = ThreadLocalRandom.NextUInt();
                Assert(expectedNext == actualNext, "State must be restored after PushState/PopState and survive extra PopState underflow");
            });
        }

        private static void Test_ThreadSafeMapPawns_ConcurrencyAndInvalidation()
        {
            SetupMockGameAndMap(1000);

            MapPawns mp = CreateMock<MapPawns>();
            List<Pawn> sampleList = new List<Pawn> { CreateMock<Pawn>(), CreateMock<Pawn>() };

            Assert(ThreadSafeMapPawns.StoreSnapshot(mp, 1, null, null) == null, "StoreSnapshot with null source must return null");
            List<Pawn> dummyOut;
            Assert(!ThreadSafeMapPawns.TryGetCached(null, 1, null, out dummyOut), "TryGetCached(null) must return false");

            List<Pawn> stored = ThreadSafeMapPawns.StoreSnapshot(mp, 5, null, sampleList);
            Assert(stored != null && stored.Count == 2, "StoreSnapshot must return a 2-element snapshot");

            List<Pawn> cached;
            Assert(ThreadSafeMapPawns.TryGetCached(mp, 5, null, out cached) && cached.Count == 2, "TryGetCached must hit for matching MapPawns/MethodId/Tick/Epoch");

            // Concurrent reads, ring list allocations, and invalidations
            Parallel.For(0, 16, i =>
            {
                for (int k = 0; k < 50; k++)
                {
                    List<Pawn> ringList = ThreadSafeMapPawns.NextThreadLocalList();
                    Assert(ringList != null && ringList.Count == 0, "NextThreadLocalList must return cleared list");
                    ringList.Add(CreateMock<Pawn>());

                    if ((k & 7) == 0)
                    {
                        ThreadSafeMapPawns.Invalidate();
                    }
                    else
                    {
                        ThreadSafeMapPawns.StoreSnapshot(mp, 5, null, sampleList);
                        List<Pawn> res;
                        if (ThreadSafeMapPawns.TryGetCached(mp, 5, null, out res))
                        {
                            Assert(res != null && res.Count == 2, "Concurrent snapshot must remain consistent (Count == 2)");
                        }
                    }
                }
            });
        }

        private static void Test_ThreadLocalCapacityCache_AdversarialInputsAndConcurrency()
        {
            float level;
            bool hasStale;
            Assert(!ThreadLocalCapacityCache.TryGet(null, null, 2500, out level, out hasStale), "TryGet(null, null) must return false");
            ThreadLocalCapacityCache.Store(null, null, 1.0f);
            ThreadLocalCapacityCache.Invalidate(null);

            Game mockGame = CreateMock<Game>();
            TickManager mockTm = CreateMock<TickManager>();
            SetField(mockTm, "ticksGameInt", 2000);
            SetField(mockGame, "tickManager", mockTm);
            SetStaticField(typeof(Current), "gameInt", mockGame);

            PawnCapacitiesHandler handler = CreateMock<PawnCapacitiesHandler>();
            PawnCapacityDef capDef = CreateMock<PawnCapacityDef>();
            capDef.index = 3;

            ThreadLocalCapacityCache.Store(handler, capDef, 0.85f);
            Assert(ThreadLocalCapacityCache.TryGet(handler, capDef, 2500, out level, out hasStale) && !hasStale && Math.Abs(level - 0.85f) < 0.0001f, "Must retrieve fresh stored capacity level");

            ThreadLocalCapacityCache.Invalidate(handler);
            Assert(!ThreadLocalCapacityCache.TryGet(handler, capDef, 2500, out level, out hasStale) && hasStale && Math.Abs(level - 0.85f) < 0.0001f, "After Invalidate(handler), TryGet must return false with hasStale=true and stale value");
        }

        private static void Test_ThreadLocalStatCache_AdversarialInputsAndConcurrency()
        {
            Assert(ThreadLocalStatCache.GetOrAdd(null, null, true, 2500) == 0f, "GetOrAdd(null, null) must return 0f");
            Assert(ThreadLocalStatCache.GetOrAddRequest(null, default(StatRequest), true, 2500) == 0f, "GetOrAddRequest(null) must return 0f");
            Assert(ThreadLocalStatCache.GetOrAddUnfinalized(null, default(StatRequest), true, 2500) == 0f, "GetOrAddUnfinalized(null) must return 0f");

            Game mockGame = CreateMock<Game>();
            TickManager mockTm = CreateMock<TickManager>();
            SetField(mockTm, "ticksGameInt", 3000);
            SetField(mockGame, "tickManager", mockTm);
            SetStaticField(typeof(Current), "gameInt", mockGame);

            MockStatWorker worker = new MockStatWorker();
            Thing thing = CreateMock<Thing>();
            FieldInfo idField = typeof(Thing).GetField("thingIDNumber", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            idField.SetValue(thing, 888);

            float v1 = ThreadLocalStatCache.GetOrAdd(worker, thing, true, 2500);
            float v2 = ThreadLocalStatCache.GetOrAdd(worker, thing, true, 2500);
            Assert(Math.Abs(v1 - 53.5f) < 0.001f && Math.Abs(v2 - 53.5f) < 0.001f, "GetOrAdd must compute and cache finalized stat value (42.5 + 10 + 1 = 53.5)");
            Assert(worker.ComputeCalls == 1, "Second GetOrAdd call within TTL must hit cache without recomputing");

            float vNoPost = ThreadLocalStatCache.GetOrAdd(worker, thing, false, 2500);
            Assert(Math.Abs(vNoPost - 43.5f) < 0.001f && worker.ComputeCalls == 2, "applyPostProcess=false must use separate modeFlags and not collide with applyPostProcess=true");

            // Advance ticksGame beyond TTL (2500 + 1024)
            SetField(mockTm, "ticksGameInt", 7000);
            worker.BaseReturnValue = 100f;
            float v3 = ThreadLocalStatCache.GetOrAdd(worker, thing, true, 2500);
            Assert(Math.Abs(v3 - 111f) < 0.001f && worker.ComputeCalls == 3, "Expired TTL must recompute stat value");
        }

        private static void Test_HarmonyPatches_LoopGuardsAndDeduplication()
        {
            MainThreadSyncQueue.InitializeOnMainThread();
            MainThreadSyncQueue.Flush();
            OmniThreadMod.EnableMultithreading = true;
            MainThreadSyncQueue.InParallelSection = true;

            try
            {
                // 1. Patch_Pawn_Kill loop guard: calling Kill twice on the same Pawn in the same tick on a worker thread must throw loop guard exception
                Pawn pawn = CreateMock<Pawn>();
                HediffSet hediffs = CreateMock<HediffSet>();
                Pawn_HealthTracker health = CreateMock<Pawn_HealthTracker>();
                SetField(health, "hediffSet", hediffs);
                SetField(health, "healthState", PawnHealthState.Mobile);
                SetField(pawn, "health", health);

                bool firstSkip = Patch_Pawn_Kill.Prefix(pawn, null, null);
                Assert(firstSkip == false, "First Patch_Pawn_Kill.Prefix in parallel section must defer and return false");

                bool loopGuardTriggered = false;
                try
                {
                    Patch_Pawn_Kill.Prefix(pawn, null, null);
                }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message.Contains("OmniThread loop guard"))
                    {
                        loopGuardTriggered = true;
                    }
                }
                Assert(loopGuardTriggered, "Second Patch_Pawn_Kill.Prefix on same Pawn in same tick must trigger loop guard");

                // 2. Patch_ReachabilityCache_Clear per-tick deduplication
                ReachabilityCache rc = CreateMock<ReachabilityCache>();
                long beforeEnqueue = MainThreadSyncQueue.TotalEnqueuedActions;
                for (int i = 0; i < 20; i++)
                {
                    bool runOrig = Patch_ReachabilityCache_Clear.Prefix(rc);
                    Assert(runOrig == false, "Patch_ReachabilityCache_Clear.Prefix must return false in parallel section");
                }
                long enqueuedDelta = MainThreadSyncQueue.TotalEnqueuedActions - beforeEnqueue;
                Assert(enqueuedDelta == 1, "Patch_ReachabilityCache_Clear must deduplicate multiple calls in the same tick to exactly 1 enqueued action");

                // 3. Patch_WealthWatcher_ForceRecount per-tick deduplication
                WealthWatcher ww = CreateMock<WealthWatcher>();
                beforeEnqueue = MainThreadSyncQueue.TotalEnqueuedActions;
                for (int i = 0; i < 20; i++)
                {
                    bool runOrig = Patch_WealthWatcher_ForceRecount.Prefix(ww, false);
                    Assert(runOrig == false, "Patch_WealthWatcher_ForceRecount.Prefix must return false in parallel section");
                }
                enqueuedDelta = MainThreadSyncQueue.TotalEnqueuedActions - beforeEnqueue;
                Assert(enqueuedDelta == 1, "Patch_WealthWatcher_ForceRecount must deduplicate multiple calls in the same tick to exactly 1 enqueued action");

                // 4. Patch_Pawn_RecordsTracker_RecordsTickInterval Finalizer suppresses NullReferenceException in parallel
                Exception suppressed = Patch_Pawn_RecordsTracker_RecordsTickInterval.Finalizer(new NullReferenceException("race in activeDriver"));
                Assert(suppressed == null, "NullReferenceException in RecordsTickInterval during parallel section must be suppressed");
                Exception notSuppressed = Patch_Pawn_RecordsTracker_RecordsTickInterval.Finalizer(new ArgumentException("other"));
                Assert(notSuppressed != null, "Non-NullReferenceException must not be suppressed");
            }
            finally
            {
                MainThreadSyncQueue.InParallelSection = false;
                // Clear queue without executing uninitialized Pawn.Kill / ReachabilityCache.Clear
                Action dummy;
                while (MainThreadSyncQueue.Queue.TryDequeue(out dummy)) { }
                MainThreadSyncQueue.Flush();
            }
        }

        private static void Test_HarmonyPatches_ReentrantLocksAndFinalizers()
        {
            OmniThreadMod.EnableMultithreading = true;
            MainThreadSyncQueue.InParallelSection = true;
            try
            {
                bool s1 = false, s2 = false, s3 = false;
                Patch_GenPlace_Lock.Prefix(ref s1);
                Patch_ReservationManager_CanReserve.Prefix(ref s2);
                Patch_GenTemperature_Lock.Prefix(ref s3);
                Assert(s1 && s2 && s3, "Lock prefixes must set __state = true when entering locks in parallel section");

                // Simulate exception recovery via Finalizers in reverse order
                Patch_GenTemperature_Lock.Finalizer(s3);
                Patch_ReservationManager_CanReserve.Finalizer(s2);
                Patch_GenPlace_Lock.Finalizer(s1);

                // Ensure locks are completely released by acquiring on another thread with short timeout
                bool locksReleased = false;
                Thread verifier = new Thread(() =>
                {
                    bool gotGlobal = Monitor.TryEnter(ParallelTickEngine.GlobalSyncLock, 500);
                    if (gotGlobal) Monitor.Exit(ParallelTickEngine.GlobalSyncLock);
                    bool gotRes = Monitor.TryEnter(ParallelTickEngine.ReservationLock, 500);
                    if (gotRes) Monitor.Exit(ParallelTickEngine.ReservationLock);
                    bool gotTemp = Monitor.TryEnter(ParallelTickEngine.GenTemperatureLock, 500);
                    if (gotTemp) Monitor.Exit(ParallelTickEngine.GenTemperatureLock);
                    locksReleased = gotGlobal && gotRes && gotTemp;
                });
                verifier.Start();
                verifier.Join();
                Assert(locksReleased, "All locks must be released after Finalizers run");
            }
            finally
            {
                MainThreadSyncQueue.InParallelSection = false;
            }
        }

        private static void Test_AutoBenchmarkRunner_EdgeCasesAndSpeedPatches()
        {
            AutoBenchmarkRunner.IsAutoTestMode = false;
            AutoBenchmarkRunner.TryTriggerQuickTestMap();
            AutoBenchmarkRunner.OnRootPlayUpdate();
            AutoBenchmarkRunner.NotifySingleTickCompleted(-1f, -5);
            AutoBenchmarkRunner.NotifySingleTickCompleted(float.NaN, 0);

            float rate = 1f;
            Assert(Patch_TickManager_TickRateMultiplier.Prefix(null, ref rate) == true && Math.Abs(rate - 1f) < 0.001f, "TickRateMultiplier.Prefix(null) must return true without modifying result");
            Patch_TickManager_TickRateMultiplier.Postfix(null, ref rate);
            Assert(Math.Abs(rate - 1f) < 0.001f, "TickRateMultiplier.Postfix(null) must not modify result");

            TickManager tm = CreateMock<TickManager>();
            SetField(tm, "curTimeSpeed", TimeSpeed.Ultrafast);

            OmniThreadMod.UncapMaxTimeSpeed = true;
            rate = 1f;
            bool runOrig = Patch_TickManager_TickRateMultiplier.Prefix(tm, ref rate);
            Assert(runOrig == false && Math.Abs(rate - 150f) < 0.001f, "UncapMaxTimeSpeed=true at Ultrafast must set rate=150f and skip original");

            OmniThreadMod.UncapMaxTimeSpeed = false;
            rate = 4f;
            runOrig = Patch_TickManager_TickRateMultiplier.Prefix(tm, ref rate);
            Assert(runOrig == true && Math.Abs(rate - 4f) < 0.001f, "UncapMaxTimeSpeed=false must let original getter run");
            OmniThreadMod.UncapMaxTimeSpeed = true;

            bool forcePaused = true;
            AutoBenchmarkRunner.IsAutoTestMode = true;
            AutoBenchmarkRunner.BenchmarkFinished = false;
            try
            {
                bool fpOrig = Patch_TickManager_ForcePaused.Prefix(ref forcePaused);
                Assert(fpOrig == false && forcePaused == false, "ForcePaused.Prefix in active AutoTestMode must set __result=false and skip original");
            }
            finally
            {
                AutoBenchmarkRunner.IsAutoTestMode = false;
            }
        }

        private static void Test_ParallelTickEngine_ExecuteTickList_AllIterationLevelsAndFaultIsolation()
        {
            SetupMockGameAndMap(1000);
            MainThreadSyncQueue.InitializeOnMainThread();
            MainThreadSyncQueue.Flush();

            FieldInfo mapIndexOrStateField = typeof(Thing).GetField("mapIndexOrState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo idField = typeof(Thing).GetField("thingIDNumber", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo posField = typeof(Thing).GetField("positionInt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            ThingDef mockDef = CreateMock<ThingDef>();
            mockDef.category = ThingCategory.Item;
            mockDef.tickerType = TickerType.Normal;

            for (int iter = 1; iter <= 5; iter++)
            {
                OmniThreadMod.IterationLevel = iter;
                OmniThreadMod.EnableMultithreading = true;

                TickList tl = CreateMock<TickList>();
                List<Thing> bucket = new List<Thing>();
                List<List<Thing>> thingLists = new List<List<Thing>> { bucket };
                List<Thing> toReg = new List<Thing> { null };
                List<Thing> toDereg = new List<Thing> { null };

                MockModThing[] healthy = new MockModThing[16];
                for (int i = 0; i < healthy.Length; i++)
                {
                    MockModThing t = CreateMock<MockModThing>();
                    t.def = mockDef;
                    mapIndexOrStateField.SetValue(t, (sbyte)0);
                    idField.SetValue(t, 50000 + iter * 100 + i);
                    posField.SetValue(t, new IntVec3(i * 8, 0, i * 8));
                    healthy[i] = t;
                    bucket.Add(t);
                }

                // Add null, destroyed, and throwing thing into bucket
                bucket.Add(null);

                MockModThing destroyed = CreateMock<MockModThing>();
                destroyed.def = mockDef;
                mapIndexOrStateField.SetValue(destroyed, (sbyte)-2);
                idField.SetValue(destroyed, 60000 + iter);
                bucket.Add(destroyed);

                MockModThing throwingThing = CreateMock<MockModThing>();
                throwingThing.def = mockDef;
                throwingThing.ThrowOnTick = true;
                mapIndexOrStateField.SetValue(throwingThing, (sbyte)0);
                idField.SetValue(throwingThing, 70000 + iter);
                posField.SetValue(throwingThing, new IntVec3(64, 0, 64));
                bucket.Add(throwingThing);

                SetField(tl, "tickType", TickerType.Normal);
                SetField(tl, "thingLists", thingLists);
                SetField(tl, "thingsToRegister", toReg);
                SetField(tl, "thingsToDeregister", toDereg);

                ParallelTickEngine.ExecuteTickList(tl);

                Assert(!MainThreadSyncQueue.InParallelSection, "InParallelSection must be false after ExecuteTickList at iteration " + iter);
                Assert(destroyed.TickCalls == 0, "Destroyed thing must not be ticked at iteration " + iter);
                for (int i = 0; i < healthy.Length; i++)
                {
                    Assert(healthy[i].TickCalls == 1, string.Format("Healthy thing #{0} must be ticked exactly once at iteration {1} (actual={2})", i, iter, healthy[i].TickCalls));
                }
            }
            OmniThreadMod.IterationLevel = 5;
        }

        private static void Test_Bug1_SpatialPartitioner_NoDuplicateSectorKeysInColorBatch()
        {
            SetupMockGameAndMap(1000);
            FieldInfo mapIndexOrStateField = typeof(Thing).GetField("mapIndexOrState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo idField = typeof(Thing).GetField("thingIDNumber", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo posField = typeof(Thing).GetField("positionInt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            List<Thing> source = new List<Thing>();
            for (int k = 0; k < 12; k++)
            {
                ParallelWorkerThing clusterThing = CreateMock<ParallelWorkerThing>();
                mapIndexOrStateField.SetValue(clusterThing, (sbyte)0);
                idField.SetValue(clusterThing, 2000 + k);
                posField.SetValue(clusterThing, new IntVec3(1, 0, 1));
                source.Add(clusterThing);
            }

            for (int iter = 2; iter <= 4; iter++)
            {
                OmniThreadMod.IterationLevel = iter;
                List<Thing> mainThreadThings;
                List<SpatialSectorBatch>[] batches = SpatialPartitioner.PartitionThings(source, out mainThreadThings);
                for (int c = 0; c < 4; c++)
                {
                    HashSet<int> uniqueSectorKeys = new HashSet<int>();
                    for (int b = 0; b < batches[c].Count; b++)
                    {
                        SpatialSectorBatch batch = batches[c][b];
                        Assert(uniqueSectorKeys.Add(batch.SectorKey),
                            string.Format("Iter {0}: Duplicate SectorKey {1} in batches[{2}] (batchCount={3}) — same 16x16 sector split across parallel threads!",
                                iter, batch.SectorKey, c, batches[c].Count));
                    }
                }
            }
            OmniThreadMod.IterationLevel = 5;
        }

        private static void Test_Bug2_MainThreadRouting_SpawnedFaulted_Skyfaller_And_Iter1()
        {
            SetupMockGameAndMap(1000);
            MainThreadSyncQueue.InitializeOnMainThread();
            MainThreadSyncQueue.Flush();

            FieldInfo mapIndexOrStateField = typeof(Thing).GetField("mapIndexOrState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo idField = typeof(Thing).GetField("thingIDNumber", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo posField = typeof(Thing).GetField("positionInt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            // Part A: SpatialPartitioner.PartitionThings must route spawned faultedThing and spawned Skyfaller to mainThreadThings
            Thing spawnedFaulted = CreateMock<Thing>();
            mapIndexOrStateField.SetValue(spawnedFaulted, (sbyte)0);
            idField.SetValue(spawnedFaulted, 3001);
            posField.SetValue(spawnedFaulted, new IntVec3(4, 0, 4));
            ModCompatibilityManager.MarkThingFaultedForMainThread(spawnedFaulted);

            Skyfaller spawnedSkyfaller = CreateMock<Skyfaller>();
            mapIndexOrStateField.SetValue(spawnedSkyfaller, (sbyte)0);
            idField.SetValue(spawnedSkyfaller, 3002);
            posField.SetValue(spawnedSkyfaller, new IntVec3(5, 0, 5));

            List<Thing> partitionSource = new List<Thing> { spawnedFaulted, spawnedSkyfaller };
            List<Thing> mtOut;
            List<SpatialSectorBatch>[] batches = SpatialPartitioner.PartitionThings(partitionSource, out mtOut);
            Assert(ContainsReference(mtOut, spawnedFaulted), "SpatialPartitioner.PartitionThings must route spawned faultedThing to mainThreadThings");
            Assert(ContainsReference(mtOut, spawnedSkyfaller), "SpatialPartitioner.PartitionThings must route spawned Skyfaller to mainThreadThings");
            for (int c = 0; c < 4; c++)
            {
                Assert(batches[c].Count == 0, "Spawned faultedThing and Skyfaller must not be placed into colorBatches[" + c + "]");
            }

            // Part B: Iteration 1 (ExecuteIteration1NaiveParallel) must execute main-thread-required and unspawned objects on MainThreadId outside InParallelSection
            OmniThreadMod.IterationLevel = 1;
            OmniThreadMod.EnableMultithreading = true;

            ThingDef mockDef = CreateMock<ThingDef>();
            mockDef.category = ThingCategory.Item;
            mockDef.tickerType = TickerType.Normal;

            TickList tl = CreateMock<TickList>();
            List<Thing> bucket = new List<Thing>();
            List<List<Thing>> thingLists = new List<List<Thing>> { bucket };
            List<Thing> toReg = new List<Thing>();
            List<Thing> toDereg = new List<Thing>();

            ParallelWorkerThing iter1Faulted = CreateMock<ParallelWorkerThing>();
            iter1Faulted.def = mockDef;
            mapIndexOrStateField.SetValue(iter1Faulted, (sbyte)0);
            idField.SetValue(iter1Faulted, 3100);
            posField.SetValue(iter1Faulted, new IntVec3(10, 0, 10));
            ModCompatibilityManager.MarkThingFaultedForMainThread(iter1Faulted);
            bucket.Add(iter1Faulted);

            ParallelWorkerThing iter1Unspawned = CreateMock<ParallelWorkerThing>();
            iter1Unspawned.def = mockDef;
            mapIndexOrStateField.SetValue(iter1Unspawned, (sbyte)-1);
            idField.SetValue(iter1Unspawned, 3101);
            bucket.Add(iter1Unspawned);

            for (int i = 0; i < 20; i++)
            {
                ParallelWorkerThing w = CreateMock<ParallelWorkerThing>();
                w.def = mockDef;
                w.SleepMsOnTick = 1;
                mapIndexOrStateField.SetValue(w, (sbyte)0);
                idField.SetValue(w, 3200 + i);
                posField.SetValue(w, new IntVec3(i * 4, 0, i * 4));
                bucket.Add(w);
            }

            SetField(tl, "tickType", TickerType.Normal);
            SetField(tl, "thingLists", thingLists);
            SetField(tl, "thingsToRegister", toReg);
            SetField(tl, "thingsToDeregister", toDereg);

            ParallelTickEngine.ExecuteTickList(tl);
            OmniThreadMod.IterationLevel = 5;

            Assert(iter1Faulted.TickCalls == 1, "Iter1 faultedThing must tick once");
            Assert(iter1Faulted.LastTickThreadId == MainThreadSyncQueue.MainThreadId && !iter1Faulted.TickedWhileInParallelSection,
                string.Format("Iter1 faultedThing must execute DoTick() on MainThreadId ({0}) outside parallel section, but ran on thread {1} (inParallel={2})",
                    MainThreadSyncQueue.MainThreadId, iter1Faulted.LastTickThreadId, iter1Faulted.TickedWhileInParallelSection));

            Assert(iter1Unspawned.TickCalls == 1, "Iter1 unspawnedThing must tick once");
            Assert(iter1Unspawned.LastTickThreadId == MainThreadSyncQueue.MainThreadId && !iter1Unspawned.TickedWhileInParallelSection,
                string.Format("Iter1 unspawnedThing must execute DoTick() on MainThreadId ({0}) outside parallel section, but ran on thread {1} (inParallel={2})",
                    MainThreadSyncQueue.MainThreadId, iter1Unspawned.LastTickThreadId, iter1Unspawned.TickedWhileInParallelSection));
        }

        private static void Test_Bug3_Iteration5_UnspawnedThingTicksOnceOnMainThread()
        {
            SetupMockGameAndMap(1000);
            MainThreadSyncQueue.InitializeOnMainThread();
            MainThreadSyncQueue.Flush();

            OmniThreadMod.IterationLevel = 5;
            OmniThreadMod.EnableMultithreading = true;

            FieldInfo mapIndexOrStateField = typeof(Thing).GetField("mapIndexOrState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo idField = typeof(Thing).GetField("thingIDNumber", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo posField = typeof(Thing).GetField("positionInt", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            ThingDef mockDef = CreateMock<ThingDef>();
            mockDef.category = ThingCategory.Item;
            mockDef.tickerType = TickerType.Normal;

            TickList tl = CreateMock<TickList>();
            List<Thing> bucket = new List<Thing>();
            List<List<Thing>> thingLists = new List<List<Thing>> { bucket };
            List<Thing> toReg = new List<Thing>();
            List<Thing> toDereg = new List<Thing>();

            // 1. Unspawned normal object (!thing.Spawned, !thing.Destroyed)
            ParallelWorkerThing unspawnedNormal = CreateMock<ParallelWorkerThing>();
            unspawnedNormal.def = mockDef;
            mapIndexOrStateField.SetValue(unspawnedNormal, (sbyte)-1);
            idField.SetValue(unspawnedNormal, 4100);
            bucket.Add(unspawnedNormal);

            // 2. Unspawned faulted/main-thread-routed object (!thing.Spawned, !thing.Destroyed)
            ParallelWorkerThing unspawnedFaulted = CreateMock<ParallelWorkerThing>();
            unspawnedFaulted.def = mockDef;
            mapIndexOrStateField.SetValue(unspawnedFaulted, (sbyte)-1);
            idField.SetValue(unspawnedFaulted, 4101);
            ModCompatibilityManager.MarkThingFaultedForMainThread(unspawnedFaulted);
            bucket.Add(unspawnedFaulted);

            // Add 22 spawned worker items so count >= 16 and Iter5 chunked workers run
            for (int i = 0; i < 22; i++)
            {
                ParallelWorkerThing w = CreateMock<ParallelWorkerThing>();
                w.def = mockDef;
                w.SleepMsOnTick = 1;
                mapIndexOrStateField.SetValue(w, (sbyte)0);
                idField.SetValue(w, 4200 + i);
                posField.SetValue(w, new IntVec3(i * 4, 0, i * 4));
                bucket.Add(w);
            }

            SetField(tl, "tickType", TickerType.Normal);
            SetField(tl, "thingLists", thingLists);
            SetField(tl, "thingsToRegister", toReg);
            SetField(tl, "thingsToDeregister", toDereg);

            ParallelTickEngine.ExecuteTickList(tl);

            Assert(unspawnedFaulted.TickCalls == 1,
                string.Format("Iter5 unspawned faulted/routed Thing must tick exactly once on MainThread (was silently skipped: TickCalls={0})", unspawnedFaulted.TickCalls));
            Assert(unspawnedFaulted.LastTickThreadId == MainThreadSyncQueue.MainThreadId && !unspawnedFaulted.TickedWhileInParallelSection,
                "Iter5 unspawned faulted Thing must tick on MainThreadId outside parallel section");

            Assert(unspawnedNormal.TickCalls == 1,
                string.Format("Iter5 unspawned normal Thing must tick exactly once (TickCalls={0})", unspawnedNormal.TickCalls));
            Assert(unspawnedNormal.LastTickThreadId == MainThreadSyncQueue.MainThreadId && !unspawnedNormal.TickedWhileInParallelSection,
                string.Format("Iter5 unspawned normal Thing must be routed to MainThreadId ({0}) outside parallel section, but ran on thread {1} (inParallel={2})",
                    MainThreadSyncQueue.MainThreadId, unspawnedNormal.LastTickThreadId, unspawnedNormal.TickedWhileInParallelSection));
        }

        private static void Test_Bug4_ParallelTickEngine_GetBucket_IntMinValueHashCode()
        {
            SetupMockGameAndMap(1000);
            FieldInfo mapIndexOrStateField = typeof(Thing).GetField("mapIndexOrState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo idField = typeof(Thing).GetField("thingIDNumber", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            MinHashThing minThing = CreateMock<MinHashThing>();
            mapIndexOrStateField.SetValue(minThing, (sbyte)-1);
            idField.SetValue(minThing, 5500);
            Assert(minThing.GetHashCode() == int.MinValue, "MinHashThing.GetHashCode() must be int.MinValue");

            List<List<Thing>> rareLists = new List<List<Thing>>(250);
            for (int i = 0; i < 250; i++)
            {
                rareLists.Add(new List<Thing>());
            }

            MethodInfo getBucketMethod = typeof(ParallelTickEngine).GetMethod("GetBucket", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert(getBucketMethod != null, "ParallelTickEngine.GetBucket method must exist");

            List<Thing> bucket;
            try
            {
                bucket = (List<Thing>)getBucketMethod.Invoke(null, new object[] { rareLists, minThing, 250 });
            }
            catch (TargetInvocationException tie)
            {
                throw tie.InnerException ?? tie;
            }
            Assert(bucket != null, "GetBucket with GetHashCode() == int.MinValue and interval = 250 must return a valid bucket without throwing ArgumentOutOfRangeException");

            // Also test via ExecuteTickList with TickerType.Rare registration
            TickList rareTl = CreateMock<TickList>();
            List<Thing> toReg = new List<Thing> { minThing };
            List<Thing> toDereg = new List<Thing>();
            SetField(rareTl, "tickType", TickerType.Rare);
            SetField(rareTl, "thingLists", rareLists);
            SetField(rareTl, "thingsToRegister", toReg);
            SetField(rareTl, "thingsToDeregister", toDereg);

            ParallelTickEngine.ExecuteTickList(rareTl);
            Assert(toReg.Count == 0, "ExecuteTickList(TickerType.Rare) must register MinHashThing without throwing ArgumentOutOfRangeException");
        }

        private static void Test_Bug5_NullSafety_PartitionThingsAndShouldTickOnMainThread()
        {
            SetupMockGameAndMap(1000);
            FieldInfo mapIndexOrStateField = typeof(Thing).GetField("mapIndexOrState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo idField = typeof(Thing).GetField("thingIDNumber", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            List<Thing> mainThreadThings;
            List<SpatialSectorBatch>[] batches = SpatialPartitioner.PartitionThings(null, out mainThreadThings);
            Assert(batches != null && batches.Length == 4, "SpatialPartitioner.PartitionThings(null, out mainThreadThings) must not throw NullReferenceException and must return 4 color batches");
            Assert(mainThreadThings != null && mainThreadThings.Count == 0, "SpatialPartitioner.PartitionThings(null) must set empty non-null mainThreadThings");

            Thing spawnedThing = CreateMock<Thing>();
            mapIndexOrStateField.SetValue(spawnedThing, (sbyte)0);
            idField.SetValue(spawnedThing, 6600);

            bool resSpawnedNullType = ModCompatibilityManager.ShouldTickOnMainThread(spawnedThing, null);
            Assert(!resSpawnedNullType, "ModCompatibilityManager.ShouldTickOnMainThread(spawnedThing, null) must not throw ArgumentNullException");

            bool resNullNull = ModCompatibilityManager.ShouldTickOnMainThread(null, null);
            Assert(!resNullNull, "ModCompatibilityManager.ShouldTickOnMainThread(null, null) must not throw ArgumentNullException and must return false");
        }

        private static void Test_Bug6_ParallelTickEngine_TelemetryCacheLinePaddingAndSync()
        {
            ParallelTickEngine.ResetThreadWorkCounts();
            int tid = Thread.CurrentThread.ManagedThreadId;

            ParallelTickEngine.BeginTickMeasurement();
            ParallelTickEngine.RecordThreadWork();
            ParallelTickEngine.RecordThreadWork();
            ParallelTickEngine.RecordThreadWork();
            ParallelTickEngine.EndTickMeasurement();
            ParallelTickEngine.SyncThreadWorkDictionary();

            long workCount;
            Assert(ParallelTickEngine.ThreadWorkCounts.TryGetValue(tid, out workCount) && workCount == 3,
                string.Format("SyncThreadWorkDictionary must record work count 3 under key tid={0} (actual={1})", tid, workCount));

            FieldInfo countsField = typeof(ParallelTickEngine).GetField("FastThreadWorkCounts", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            FieldInfo stampsField = typeof(ParallelTickEngine).GetField("FastThreadSeenStamp", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert(countsField != null && stampsField != null, "FastThreadWorkCounts and FastThreadSeenStamp fields must exist");

            Array countsArr = countsField.GetValue(null) as Array;
            Array stampsArr = stampsField.GetValue(null) as Array;
            Assert(countsArr != null && stampsArr != null, "Telemetry backing arrays must not be null");

            // Check that consecutive thread IDs are padded by at least a 64-byte cache line (stride >= 8 for long[], stride >= 16 for int[], or >= 64B struct element)
            long[] longCounts = countsArr as long[];
            int[] intStamps = stampsArr as int[];
            if (longCounts != null && intStamps != null)
            {
                Assert(longCounts.Length >= 16384 * 8,
                    string.Format("FastThreadWorkCounts (long[]) has length {0} (unpadded 8-byte stride causes L1/L2 cache-line ping-pong / false sharing; expected >= {1} for 64-byte cache line stride)",
                        longCounts.Length, 16384 * 8));
                Assert(intStamps.Length >= 16384 * 16,
                    string.Format("FastThreadSeenStamp (int[]) has length {0} (unpadded 4-byte stride causes L1/L2 cache-line ping-pong / false sharing; expected >= {1} for 64-byte cache line stride)",
                        intStamps.Length, 16384 * 16));
            }
            else
            {
                int elemSize = System.Runtime.InteropServices.Marshal.SizeOf(countsArr.GetType().GetElementType());
                Assert(elemSize >= 64, "Telemetry struct element size must be >= 64 bytes to prevent false sharing");
            }
        }
    }
}
