# OmniThread Ultimate — Lock-Free Multi-Threaded Engine for RimWorld 1.5 / 1.6

**OmniThread Ultimate** to wielowątkowy silnik optymalizacyjny C# (Harmony) dla **RimWorld 1.5 / 1.6**, który zrównolegla główną pętlę symulacji gry (`TickList.Tick()`, `Pawn.DoTick()`, `Pawn_NeedsTracker`, `AttackTargetsCache`, `MapPostTick`) na wszystkie wątki logiczne procesora przy zachowaniu **100% deterministycznego bezpieczeństwa pamięci (`caughtConcurrencyExceptions: 0`)**.

---

## Kluczowe Funkcje (Key Features)

1. **Dedykowana Pula Stałych Wątków (`OmniThread_Worker`) + Work-Stealing:**
   - Zamiast standardowego `Parallel.For` (który cierpi na problem skupienia ciężkich obiektów `Pawn` na końcu listy `TickList`), mod utrzymuje stałe wątki robocze budzone niskopoziomową barierą `ManualResetEventSlim`.
   - Przeplatanie pionków (`Interleaved LPT`) i atomowe podkradanie zadań (`Interlocked.Increment`) gwarantują równomierne obciążenie **wszystkich wątków CPU (12/12)** w każdym ticku.
2. **Architektura Lock-Free (`[ThreadStatic]` + Transpilatory IL Harmony):**
   - Bezkolizyjny `ThreadLocalBFSWorker` dla `RegionTraverser.BreadthFirstTraverse` — wszystkie wątki mogą jednocześnie przeszukiwać regiony mapy (BFS) bez globalnych blokad.
   - Bezkolizyjna pula `[ThreadStatic]` dla `GenClosest.RegionwiseBFSWorker` (`ClosestThingReachable`).
   - Transpilatory IL Harmony podmieniające statyczne bufory w `Pawn_HealthTracker` (`tmpHediffs`, `tmpRemovedHediffs`) oraz `Pawn_InteractionsTracker` (`workingList`) na izolowane bufory wątkowe.
   - Izolacja generatora liczb losowych `Verse.Rand` per wątek (`[ThreadStatic]`).
3. **Atomowa Kolejka Synchronizacji Głównego Wątku (`MainThreadSyncQueue`):**
   - Operacje wymagające głównego wątku silnika Unity (spawnowanie/niszczenie obiektów `SpawnSetup`/`DeSpawn`/`Destroy`, aktualizacja siatek mapy, dźwięki, przerywanie zadań AI) są przechwytywane i wykonywane deterministycznie na końcu ticku.
4. **Pełna Kompatybilność z Modami i Menedżerami Modów (RimPy / RimSort):**
   - **RimPy Mod Manager & RimSort:** Pełna obsługa automatycznego sortowania (`About/About.xml` + `About/Manifest.xml` + `LoadFolders.xml` + `About/Preview.png`).
   - **Wbudowane profile + Auto-Guard dla 60+ modów:**
     - **RocketMan (`vr.missilegirl`)** — bezblokadowe obejścia `StatWorker_Patch`, `SignatureUtility`, `GenTemperature_Patch` oraz wyłączenie zduplikowanej dylatacji czasu.
     - **CAI-5000 / CombatAI (`Krkr.rule56`)** — Pre-Warming `CombatAI.CompCache` na głównym wątku (100% trafień `TryGetValue` bez zapisów z wątków roboczych) oraz synchronizacja `SightTracker`, `AvoidanceTracker`, `CoverPositionFinder` i `RegionFlooder`.
     - **Combat Extended (`CETeam.CombatExtended`)** — bezpieczny routing balistyki `ProjectileCE` oraz synchronizacja `LightingTracker`.
     - **Vehicle Framework (`SmashPhil.VehicleFramework`)** — bezpieczny routing wielokafelkowych pojazdów `VehiclePawn`.
     - **Vanilla Expanded Framework (`VEF`)**, **Vanilla Psycasts Expanded**, **Vanilla Social Interactions Expanded**, **Vanilla Memes Expanded**.
     - **Zoology Mod**, **RunAndGun**, **Simple Sidearms**, **Pick Up And Haul**, **CommonSense**, **Smart Medicine**, **Hospitality**, **Dubs Bad Hygiene**, **Performance Optimizer**.
     - **Universal Auto-Guard (`ScanAndProtectThirdPartyModHooks`)** — automatyczny skaner wykrywający i zabezpieczający patche Harmony nieznanych modów na ścieżce `Tick`.

---

## Zweryfikowane Wyniki Benchmarków (AMD Ryzen 5 5600 — 6 Rdzeni / 12 Wątków)

### 1. RimWorld 1.6 + 5 DLC (~291 walczących pionków, 3000 ticków)

| Konfiguracja | Średni czas ticku (`avgMsPerTick`) | Mediana ticku (`p50`) | Compute TPS (`DoSingleTick`) | `TickList` Compute TPS | Wall-Clock TPS | Błędy (`caught`) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: |
| **1 Wątek (Vanilla Baseline)** | `6.1355 ms` | `5.0419 ms` | `162.99 TPS` | `~177.0 TPS` | `119.60 TPS` | `0` |
| **12 Wątków (`OmniThread Ultimate`)** | **`2.9323 ms`** | **`1.1169 ms` (4.51x)** | **`341.02 TPS` (+109.2%)** | **`402.44 TPS` (+127.4%)** | **`245.96 TPS` (2.06x)** | **`0`** |

### 2. Pełna Paczka 57 Modów (50 Modów + 5 DLC — `CE` + `CAI-5000` + `RocketMan` + `VEF` + `Zoology`, ~268 pionków, 3000 ticków)

| Konfiguracja | Średni czas ticku | Mediana ticku (`p50`) | Min czas ticku | Compute TPS | `TickList` TPS | Wall-Clock TPS | Aktywne Wątki | Błędy (`caught`) |
| :--- | :---: | :---: | :---: | :---: | :---: | :---: | :---: | :---: |
| **1 Wątek (57 Modów Baseline)** | `11.2229 ms` | `7.7636 ms` | `3.4217 ms` | `89.10 TPS` | `104.85 TPS` | `64.09 TPS` | `1 / 12` | `0` |
| **12 Wątków (`OmniThread Ultimate`)** | **`8.06–8.54 ms`** | **`4.70–5.17 ms` (+50–65%)** | **`1.43 ms` (2.39x)** | **`117–124 TPS` (+31–39%)** | **`142–160 TPS` (+36–53%)** | **`85.2–91.3 TPS` (+33–42%)** | **`12.00 / 12`** | **`0`** |

---

## Instalacja (RimPy / Steam / Ręczna)

1. Sklonuj repozytorium lub pobierz paczkę bezpośrednio do folderu `RimWorld/Mods/OmniThreadUltimate`:
   ```bash
   git clone https://github.com/ignxo/OmniThread-Ultimate.git "OmniThreadUltimate"
   ```
2. W **RimPy Mod Manager** (lub **RimSort**):
   - Aktywuj mod **OmniThread Ultimate** (`omnithread.ultimate`).
   - Kliknij **Sort** — dzięki regułom `<loadAfter>` w `About/About.xml` oraz `About/Manifest.xml`, RimPy automatycznie umieści mod na samym dole listy ładowania (za `Harmony`, `Vanilla Expanded Framework`, `Combat Extended`, `CAI-5000` oraz `RocketMan`).
3. Podczas gry naciśnij klawisz **`F11`**, aby włączyć lub ukryć nakładkę telemetryczną pokazującą na żywo TPS, podział pracy na poszczególne wątki CPU oraz liczbę przechwyconych kolizji wątków.

---

## Struktura Repozytorium

- `About/About.xml` & `About/Manifest.xml` — Metadane moda i reguły sortowania dla **RimPy** / **RimSort**.
- `LoadFolders.xml` — Konfiguracja ścieżek ładowania dla RimWorld `1.4`, `1.5` oraz `1.6`.
- `1.6/Assemblies/OmniThreadUltimate.dll` — Skompilowana biblioteka moda.
- `Source/OmniThreadUltimate/` — Pełny kod źródłowy C# (`ParallelTickEngine.cs`, `ModCompatibilityManager.cs`, `MainThreadSyncQueue.cs`, `OmniThreadMod.cs`, `OmniThreadOverlayGUI.cs`, `AutoBenchmarkRunner.cs`).
- `Benchmarks/` — Zapisane raporty JSON z testów na żywym procesie `RimWorldWin64.exe`.
