namespace Bikepark.Sim.Trails;

/// <summary>A point where two ways cross without a junction: the distance along each.</summary>
public readonly record struct WayCrossing(int WayA, long CmA, int WayB, long CmB)
{
    /// <summary>The same crossing seen from <paramref name="wayId"/>: (distance along it, the other way, distance along that).</summary>
    public (long Cm, int OtherWay, long OtherCm) From(int wayId) => wayId == WayA ? (CmA, WayB, CmB) : (CmB, WayA, CmA);
}

/// <summary>
/// Where two way centrelines cross (integer geometry, derived). Crossings near either way's ends are left out: ways meet
/// there on purpose (joins, hubs), and a junction is not a crossing.
/// </summary>
public static class Crossings
{
    /// <summary>Crossings closer than this to a way's start or end don't count.</summary>
    public const long EndMarginCm = 800;

    /// <summary>Two hits closer than this along the first way are one crossing (ways touching along a stretch).</summary>
    private const long MergeCm = 1_500;

    private const int Chunk = 16;

    /// <summary>Crossings of <paramref name="a"/> and <paramref name="b"/>, ordered along <paramref name="a"/>.</summary>
    public static List<(long CmA, long CmB)> Find(WayGeometry a, WayGeometry b)
    {
        var hits = new List<(long CmA, long CmB)>();
        var ax = a.Xs; var az = a.Zs; var ad = a.Distances;
        var bx = b.Xs; var bz = b.Zs; var bd = b.Distances;
        int na = ax.Length - 1, nb = bx.Length - 1;
        if (na < 1 || nb < 1) return hits;

        for (int ca = 0; ca < na; ca += Chunk)
        {
            int ea = Math.Min(na, ca + Chunk);
            var boxA = Box(ax, az, ca, ea);
            for (int cb = 0; cb < nb; cb += Chunk)
            {
                int eb = Math.Min(nb, cb + Chunk);
                if (!Overlap(boxA, Box(bx, bz, cb, eb))) continue;
                for (int i = ca; i < ea; i++)
                for (int j = cb; j < eb; j++)
                {
                    if (!Intersect(ax[i], az[i], ax[i + 1], az[i + 1], bx[j], bz[j], bx[j + 1], bz[j + 1], out long tNum, out long uNum, out long den))
                        continue;
                    long cmA = ad[i] + (ad[i + 1] - ad[i]) * tNum / den;
                    long cmB = bd[j] + (bd[j + 1] - bd[j]) * uNum / den;
                    if (cmA < EndMarginCm || cmA > a.LengthCm - EndMarginCm || cmB < EndMarginCm || cmB > b.LengthCm - EndMarginCm)
                        continue;
                    hits.Add((cmA, cmB));
                }
            }
        }

        hits.Sort((p, q) => p.CmA.CompareTo(q.CmA) is var c && c != 0 ? c : p.CmB.CompareTo(q.CmB));
        var merged = new List<(long CmA, long CmB)>();
        foreach (var hit in hits)
            if (merged.Count == 0 || hit.CmA - merged[^1].CmA >= MergeCm)
                merged.Add(hit);
        return merged;
    }

    private static (int MinX, int MinZ, int MaxX, int MaxZ) Box(ReadOnlySpan<int> xs, ReadOnlySpan<int> zs, int from, int to)
    {
        int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
        for (int i = from; i <= to; i++)
        {
            minX = Math.Min(minX, xs[i]); maxX = Math.Max(maxX, xs[i]);
            minZ = Math.Min(minZ, zs[i]); maxZ = Math.Max(maxZ, zs[i]);
        }
        return (minX, minZ, maxX, maxZ);
    }

    private static bool Overlap((int MinX, int MinZ, int MaxX, int MaxZ) p, (int MinX, int MinZ, int MaxX, int MaxZ) q) =>
        p.MinX <= q.MaxX && q.MinX <= p.MaxX && p.MinZ <= q.MaxZ && q.MinZ <= p.MaxZ;

    /// <summary>
    /// Segments p1-p2 and q1-q2 cross: the hit is at p1 + (p2 - p1) * tNum / den = q1 + (q2 - q1) * uNum / den, with
    /// 0 &lt;= tNum, uNum &lt;= den (den &gt; 0). Parallel or touching-only segments don't count.
    /// </summary>
    internal static bool Intersect(long p1x, long p1z, long p2x, long p2z, long q1x, long q1z, long q2x, long q2z,
        out long tNum, out long uNum, out long den)
    {
        long rx = p2x - p1x, rz = p2z - p1z, sx = q2x - q1x, sz = q2z - q1z;
        den = rx * sz - rz * sx;
        long wx = q1x - p1x, wz = q1z - p1z;
        tNum = wx * sz - wz * sx;
        uNum = wx * rz - wz * rx;
        if (den == 0) return false;
        if (den < 0)
        {
            den = -den;
            tNum = -tNum;
            uNum = -uNum;
        }
        // Half-open on the far end, so a hit exactly on a shared sample counts once.
        return tNum >= 0 && tNum < den && uNum >= 0 && uNum < den;
    }
}
