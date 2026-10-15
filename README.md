# OmniThread Ultimate — Lock-Free Multi-Threaded Engine for RimWorld (1.3 – 1.7)

![RimWorld 1.3-1.7](https://img.shields.io/badge/RimWorld-1.3%20%7C%201.4%20%7C%201.5%20%7C%201.6%20%7C%201.7-00d2ff?style=for-the-badge)
![License MIT](https://img.shields.io/badge/License-MIT-50fa96?style=for-the-badge)
![Threads 12+](https://img.shields.io/badge/Multi--Threading-Work--Stealing%20Pool-ff9e3b?style=for-the-badge)
![RimPy Compatible](https://img.shields.io/badge/RimPy%20%26%20RimSort-Auto--Sort%20Ready-a970ff?style=for-the-badge)

**OmniThread Ultimate** is a high-performance C# (Harmony) multi-threading optimization mod for **RimWorld (1.3, 1.4, 1.5, 1.6, and 1.7)**. It parallelizes RimWorld's core simulation loop (`TickList.Tick()`, `Pawn.DoTick()`, `Pawn_NeedsTracker`, `AttackTargetsCache`, and `MapPostTick`) across all logical CPU cores while maintaining **100% deterministic memory safety (`caughtConcurrencyExceptions: 0`)** and broad compatibility with complex overhaul mods.

---

## Key Architecture & Features

### 1. Dedicated Persistent Worker Pool (`OmniThread_Worker`) + Atomic Work-Stealing
- **Zero ThreadPool Starvation:** Instead of relying on standard `Parallel.For` range partitioning (which suffers from severe tail-skew because RimWorld appends heavy `Pawn` instances at the very end of `TickList` buckets), OmniThread Ultimate maintains persistent background threads synchronized via low-overhead `ManualResetEventSlim` barriers.
- **Interleaved LPT Scheduling & Atomic Work-Stealing:** Heavy `Pawn` entities are interleaved with lightweight `Thing` entities and consumed atomically via `Interlocked.Increment`, ensuring **all 12+ logical CPU threads remain 100% active** until the final pawn finishes its tick.

### 2. Lock-Free Core Subsystems (`[ThreadStatic]` & Harmony IL Transpilers)
- **Lock-Free `ThreadLocalBFSWorker` (`RegionTraverser.BreadthFirstTraverse`):** Replaces RimWorld's single-threaded `freeWorkers` queue and shared `region.reachedIndex` mutations with per-thread BFS worker pools and isolated `closedRegionIds` sets. Multiple threads can traverse regions simultaneously with zero lock contention.
- **Lock-Free `GenClosest.RegionwiseBFSWorker` Pool:** Per-thread `[ThreadStatic]` pools for `RegionProcessorClosestThingReachable`.
- **Harmony IL Transpilers:** Rewrites static shared collections inside `Pawn_HealthTracker` (`tmpHediffs`, `tmpRemovedHediffs`) and `Pawn_InteractionsTracker` (`workingList`) into thread-local buffers.
- **Per-Thread Deterministic RNG (`Verse.Rand`):** Isolates RNG state stacks and seeds per worker thread to prevent random number corruption or state-stack crashes.

### 3. Atomic `MainThreadSyncQueue`
- Operations that mutate Unity Engine objects, map grids, or global registries (`Thing.SpawnSetup`, `Thing.DeSpawn`, `Thing.Destroy`, `GenSpawn.Spawn`, `FleckManager`, `MoteMaker`, `SoundStarter`, `MapPawns`) are automatically intercepted on worker threads and flushed deterministically on the main thread at the end of each tick.

### 4. Built-In Mod Compatibility & RimPy Auto-Sorting
- **RimPy Mod Manager & RimSort Ready:** Includes complete `<loadAfter>` rules in `About/About.xml` and `About/Manifest.xml` so clicking **Sort** in **RimPy** or **RimSort** automatically places **OmniThread Ultimate** in the optimal load order position.
- **Dedicated Profiles for 60+ Popular Mods:**
  - **RocketMan (`vr.missilegirl`)** — Lock-free parallel bypasses for `StatWorker_Patch`, `SignatureUtility`, and `GenTemperature_Patch`, plus automatic deduplication of `WorldPawns` time dilation.
  - **CAI-5000 / CombatAI (`Krkr.rule56`)** — Main-thread `CompCache` pre-warming (guaranteeing 100% lock-free `TryGetValue` cache hits on worker threads) and thread-safe synchronization for `SightTracker`, `AvoidanceTracker`, `CoverPositionFinder`, `RegionFlooder`, and `CellFlooder`.
  - **Combat Extended (`CETeam.CombatExtended`)** — Safe main-thread routing for ballistic `ProjectileCE` trajectories and thread-safe `LightingTracker` synchronization.
  - **Vehicle Framework (`SmashPhil.VehicleFramework`)** & **Vanilla Vehicles Expanded** — Safe main-thread routing for multi-cell `VehiclePawn` instances.
  - **Vanilla Expanded Framework (`VEF`)**, **Vanilla Psycasts Expanded**, **Vanilla Social Interactions Expanded**, **Vanilla Memes Expanded**.
  - **Zoology Mod**, **RunAndGun**, **Simple Sidearms**, **Pick Up And Haul**, **CommonSense**, **Smart Medicine**, **Allow Tool**, **Hospitality**, **Dubs Bad Hygiene**, **Performance Optimizer**.
- **Universal Harmony Auto-Guard (`ScanAndProtectThirdPartyModHooks`):** Automatically inspects all loaded third-party Harmony `Prefix`/`Postfix` hooks attached to RimWorld's core tick methods at startup and wraps unguarded third-party mod callbacks in re-entrant synchronization (`|L| = 1`, mathematically eliminating AB-BA deadlocks).
- **Resilient Cross-Version Patching (`1.3` / `1.4` / `1.5` / `1.6` / `1.7`):** Dynamically adapts to API differences across RimWorld versions (`HealthTick` vs `HealthTickInterval`, `PathFinder.FindPath` vs `FindPathNow`).

---

## Verified Live Benchmarks (AMD Ryzen 5 5600 — 6 Cores / 12 Threads)

### 1. RimWorld 1.6 + 5 Official DLCs (~291 Active Pawns in Combat, 3,000 Ticks)

| Configuration | Avg Tick Time (`avgMsPerTick`) | Median Tick (`p50`) | Compute TPS (`DoSingleTick`) | `TickList` Compute TPS | Wall-Clock TPS | Concurrency Exceptions |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **1 Thread (Vanilla Baseline)** | `6.1355 ms` | `5.0419 ms` | `162.99 TPS` | `~177.0 TPS` | `119.60 TPS` | `0` |
| **12 Threads (`OmniThread Ultimate`)** | **`2.9323 ms`** | **`1.1169 ms` (4.51x)** | **`341.02 TPS` (+109.2%)** | **`402.44 TPS` (+127.4%)** | **`245.96 TPS` (2.06x)** | **`0`** |

### 2. Heavy 57-Mod Combat Stress Test (50 Mods + 5 DLCs — `CE` + `CAI-5000` + `RocketMan` + `VEF` + `Zoology`, ~268 Pawns, 3,000 Ticks)

| Configuration | Avg Tick Time | Median Tick (`p50`) | Min Tick Time | Compute TPS | `TickList` TPS | Wall-Clock TPS | Active Threads | Concurrency Exceptions |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **1 Thread (57 Mods Baseline)** | `11.2229 ms` | `7.7636 ms` | `3.4217 ms` | `89.10 TPS` | `104.85 TPS` | `64.09 TPS` | `1 / 12` | `0` |
| **12 Threads (`OmniThread Ultimate`)** | **`8.06–8.54 ms`** | **`4.70–5.17 ms` (+50–65%)** | **`1.43 ms` (2.39x)** | **`117–124 TPS` (+31–39%)** | **`142–160 TPS` (+36–53%)** | **`85.2–91.3 TPS` (+33–42%)** | **`12.00 / 12`** | **`0`** |

---

## Installation (RimPy / RimSort / Manual)

1. Clone this repository or extract the latest release `.zip` into your `RimWorld/Mods/` directory:
   ```bash
   git clone https://github.com/ignxo/OmniThread-Ultimate.git "OmniThreadUltimate"
   ```
2. Open **RimPy Mod Manager** (or **RimSort** / in-game Mod Manager):
   - Enable **Harmony** (`brrainz.harmony`) and **OmniThread Ultimate** (`omnithread.ultimate`).
   - Click **Sort** — RimPy will automatically place **OmniThread Ultimate** near the bottom of your load order after frameworks and overhaul mods.
3. Press **`F11`** in-game at any time to toggle the real-time **OmniThread Ultimate Telemetry Overlay** (displaying live TPS, per-thread workload distribution, active worker threads, and thread-safety counters).

---

## Repository Structure

- `About/About.xml` & `About/Manifest.xml` — Mod metadata and load-order rules for **RimPy**, **RimSort**, and **Mod Manager**.
- `LoadFolders.xml` — Multi-version folder mapping for RimWorld `1.3`, `1.4`, `1.5`, `1.6`, and `1.7`.
- `1.6/Assemblies/OmniThreadUltimate.dll` — Compiled assembly.
- `Source/OmniThreadUltimate/` — Full C# source code (`ParallelTickEngine.cs`, `ModCompatibilityManager.cs`, `MainThreadSyncQueue.cs`, `OmniThreadMod.cs`, `OmniThreadGUI.cs`, `AutoBenchmarkRunner.cs`, `SpatialPartitioner.cs`).
- `Benchmarks/` — Verified JSON benchmark reports captured from live `RimWorldWin64.exe` runs.

---

## License

This project is licensed under the **MIT License** — see the [LICENSE](LICENSE) file for details.
