using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Trails;

/// <summary>
/// Finds the rocks on a way's line from the rocks that stand there (<see cref="TerrainScatter"/>), never from the ground layer
/// under it: what you see is what the rules see. A boulder or face counts where its footprint reaches the way; stones count
/// where they lie close to the line, and many close together over a few meters are a stone field. Pure and integer-only.
/// </summary>
internal static class RockFinder
{
    /// <summary>Collection tiles: a multiple of every rock cell (2, 6 m).</summary>
    private const int TileMeters = 24;

    /// <summary>The farthest a rock's centre can lie from the line and still reach it (the biggest face).</summary>
    private const int BigReachCm = 2000;

    /// <summary>Half the width that has to be free of boulders and faces: a trail's deck, a path's gravel.</summary>
    public const int TrailHalfWidthCm = 90, PathHalfWidthCm = 150;

    /// <summary>Stones this close to the line count towards a stone field.</summary>
    private const int FieldReachCm = 300;

    /// <summary>Stones this much beyond the way's half width are cleared by the crew.</summary>
    private const int ClearMarginCm = 50;

    /// <summary>A stone field: at least this many stones near a 7 m stretch (±3 samples).</summary>
    private const int FieldWindow = 3, FieldStones = 6;

    /// <summary>Gaps in a field up to this many samples are still field.</summary>
    private const int FieldGapSamples = 2;

    /// <summary>A field shorter than this (samples) is only a few stones: cleared.</summary>
    private const int MinFieldSamples = 5;

    private const int BucketCm = 400;

    public static List<WayRock> Find(TerrainGrid grid, WayKind kind, int[] x, int[] z)
    {
        var rocks = new List<WayRock>();
        if (!grid.HasRockFormations || x.Length == 0) return rocks;
        int n = x.Length;
        int halfWidth = kind == WayKind.Trail ? TrailHalfWidthCm : PathHalfWidthCm;

        // The samples in buckets, for nearest-sample lookups.
        var buckets = new Dictionary<long, List<int>>();
        for (int i = 0; i < n; i++)
        {
            long key = Key(x[i] / BucketCm, z[i] / BucketCm);
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
            list.Add(i);
        }

        var cls = new byte[n]; // 0 none, 1 boulder, 2 face
        var stones = new int[n];
        var clear = new bool[n];
        foreach (var rock in RocksNear(grid, x, z))
        {
            var kindOf = RockClasses.Of(rock);
            if (kindOf == RockClass.Stone)
            {
                if (Nearest(buckets, x, z, rock.XCm, rock.ZCm, FieldReachCm) is not var (at, d2)) continue;
                stones[at]++;
                long reach = halfWidth + ClearMarginCm;
                if (d2 <= reach * reach) clear[at] = true;
                continue;
            }
            long r = RockShapes.RadiusCm(rock) + halfWidth;
            byte mark = kindOf == RockClass.Face ? (byte)2 : (byte)1;
            ForEachWithin(buckets, x, z, rock.XCm, rock.ZCm, r, i => cls[i] = Math.Max(cls[i], mark));
        }

        // Stone fields: enough stones near a short stretch, small gaps closed.
        var field = new bool[n];
        long window = 0;
        for (int i = 0; i < Math.Min(n, FieldWindow); i++) window += stones[i];
        for (int i = 0; i < n; i++)
        {
            if (i + FieldWindow < n) window += stones[i + FieldWindow];
            if (i - FieldWindow - 1 >= 0) window -= stones[i - FieldWindow - 1];
            field[i] = window >= FieldStones;
        }
        for (int i = 0, last = -1; i < n; i++)
        {
            if (!field[i]) continue;
            if (last >= 0 && i - last - 1 <= FieldGapSamples)
                for (int j = last + 1; j < i; j++) field[j] = true;
            last = i;
        }

        var label = new RockFormation[n];
        for (int i = 0; i < n; i++)
            label[i] = cls[i] == 2 ? RockFormation.RockFace
                : cls[i] == 1 ? RockFormation.MediumRock
                : field[i] ? RockFormation.DenseStones
                : clear[i] ? RockFormation.SparseStones
                : RockFormation.None;

        for (int from = 0, i = 1; i <= n; i++)
        {
            if (i < n && label[i] == label[from]) continue;
            var k = label[from];
            int needed = k switch { RockFormation.None => int.MaxValue, RockFormation.DenseStones => MinFieldSamples, _ => 1 };
            if (i - from >= needed) rocks.Add(new WayRock(k, from, i - 1));
            else if (k == RockFormation.DenseStones) // too short for a field: its stones are cleared
                rocks.Add(new WayRock(RockFormation.SparseStones, from, i - 1));
            from = i;
        }
        return rocks;
    }

    /// <summary>Every rock that could reach the line: collected tile by tile along it, each tile once.</summary>
    private static List<ScatterInstance> RocksNear(TerrainGrid grid, int[] x, int[] z)
    {
        var seen = new HashSet<long>();
        var tiles = new List<(int X, int Z)>();
        int tileCm = TileMeters * 100;
        for (int i = 0; i < x.Length; i++)
            for (int tz = Math.Max(0, (z[i] - BigReachCm) / tileCm); tz <= (z[i] + BigReachCm) / tileCm; tz++)
                for (int tx = Math.Max(0, (x[i] - BigReachCm) / tileCm); tx <= (x[i] + BigReachCm) / tileCm; tx++)
                    if (seen.Add(Key(tx, tz))) tiles.Add((tx, tz));
        var found = new List<ScatterInstance>();
        foreach (var (tx, tz) in tiles)
            TerrainScatter.Collect(grid, tx * TileMeters, tz * TileMeters, (tx + 1) * TileMeters, (tz + 1) * TileMeters, found, rocksOnly: true);
        found.RemoveAll(r => r.Kind != ScatterKind.Rock);
        return found;
    }

    private static long Key(int bx, int bz) => ((long)bx << 32) ^ (uint)bz;

    /// <summary>The sample closest to a point within a reach (ties: the earlier sample), and its squared distance.</summary>
    private static (int At, long D2)? Nearest(Dictionary<long, List<int>> buckets, int[] x, int[] z, int px, int pz, long reach)
    {
        int best = -1;
        long bestD2 = reach * reach;
        for (long bz = (pz - reach) / BucketCm; bz <= (pz + reach) / BucketCm; bz++)
            for (long bx = (px - reach) / BucketCm; bx <= (px + reach) / BucketCm; bx++)
            {
                if (!buckets.TryGetValue(Key((int)bx, (int)bz), out var list)) continue;
                foreach (int i in list)
                {
                    long dx = x[i] - px, dz = z[i] - pz, d2 = dx * dx + dz * dz;
                    if (d2 < bestD2 || (d2 == bestD2 && (best < 0 || i < best)))
                    {
                        bestD2 = d2;
                        best = i;
                    }
                }
            }
        return best < 0 ? null : (best, bestD2);
    }

    private static void ForEachWithin(Dictionary<long, List<int>> buckets, int[] x, int[] z, int px, int pz, long reach, Action<int> visit)
    {
        for (long bz = (pz - reach) / BucketCm; bz <= (pz + reach) / BucketCm; bz++)
            for (long bx = (px - reach) / BucketCm; bx <= (px + reach) / BucketCm; bx++)
            {
                if (!buckets.TryGetValue(Key((int)bx, (int)bz), out var list)) continue;
                foreach (int i in list)
                {
                    long dx = x[i] - px, dz = z[i] - pz;
                    if (dx * dx + dz * dz <= reach * reach) visit(i);
                }
            }
    }
}
