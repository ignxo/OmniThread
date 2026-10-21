using System;
using System.Collections.Generic;
using Verse;

namespace OmniThreadUltimate
{
    public sealed class SpatialSectorBatch
    {
        public int SectorKey;
        public int ColorIndex;
        public readonly List<Thing> Things = new List<Thing>(32);

        public void Clear()
        {
            Things.Clear();
        }
    }

    public static class SpatialPartitioner
    {
        public const int SectorSize = 16;
        public const int ColorCount = 4;

        private static readonly List<SpatialSectorBatch>[] ColorBatches = new List<SpatialSectorBatch>[ColorCount]
        {
            new List<SpatialSectorBatch>(64),
            new List<SpatialSectorBatch>(64),
            new List<SpatialSectorBatch>(64),
            new List<SpatialSectorBatch>(64)
        };

        private static readonly Dictionary<int, SpatialSectorBatch> ActiveSectorsByKey = new Dictionary<int, SpatialSectorBatch>(256);
        private static readonly Stack<SpatialSectorBatch> BatchPool = new Stack<SpatialSectorBatch>(256);
        private static readonly List<Thing> MainThreadFallbackThings = new List<Thing>(128);

        public static int LastPartitionedThingCount = 0;
        public static int LastActiveSectorCount = 0;

        public static List<SpatialSectorBatch>[] PartitionThings(List<Thing> sourceList, out List<Thing> mainThreadThings)
        {
            RecycleBatches();
            MainThreadFallbackThings.Clear();

            if (sourceList == null)
            {
                LastPartitionedThingCount = 0;
                LastActiveSectorCount = 0;
                mainThreadThings = MainThreadFallbackThings;
                return ColorBatches;
            }

            int count = sourceList.Count;
            LastPartitionedThingCount = count;

            for (int i = 0; i < count; i++)
            {
                Thing thing = sourceList[i];
                if (thing == null || thing.Destroyed)
                {
                    continue;
                }

                if (!thing.Spawned || thing.Map == null)
                {
                    MainThreadFallbackThings.Add(thing);
                    continue;
                }

                if (ModCompatibilityManager.ShouldTickOnMainThread(thing, thing.GetType()))
                {
                    System.Threading.Interlocked.Increment(ref ModCompatibilityManager.MainThreadRoutedModThingTicks);
                    MainThreadFallbackThings.Add(thing);
                    continue;
                }

                IntVec3 pos = thing.Position;
                int sx = pos.x >> 4;
                int sz = pos.z >> 4;
                int mapId = thing.Map.uniqueID & 0xFF;
                int color = ((sx & 1) << 1) | (sz & 1);
                int sectorKey = (mapId << 20) | ((sx & 0x3FF) << 10) | (sz & 0x3FF);

                SpatialSectorBatch batch;
                if (!ActiveSectorsByKey.TryGetValue(sectorKey, out batch))
                {
                    batch = RentBatch(sectorKey, color);
                    ActiveSectorsByKey.Add(sectorKey, batch);
                    ColorBatches[color].Add(batch);
                }
                batch.Things.Add(thing);
            }

            LastActiveSectorCount = ActiveSectorsByKey.Count;
            mainThreadThings = MainThreadFallbackThings;
            return ColorBatches;
        }

        private static SpatialSectorBatch RentBatch(int sectorKey, int color)
        {
            SpatialSectorBatch batch;
            if (BatchPool.Count > 0)
            {
                batch = BatchPool.Pop();
            }
            else
            {
                batch = new SpatialSectorBatch();
            }
            batch.SectorKey = sectorKey;
            batch.ColorIndex = color;
            batch.Things.Clear();
            return batch;
        }

        private static void RecycleBatches()
        {
            for (int c = 0; c < ColorCount; c++)
            {
                List<SpatialSectorBatch> list = ColorBatches[c];
                for (int i = 0; i < list.Count; i++)
                {
                    SpatialSectorBatch batch = list[i];
                    batch.Clear();
                    BatchPool.Push(batch);
                }
                list.Clear();
            }
            ActiveSectorsByKey.Clear();
        }
    }
}
