namespace Bikepark.Sim.Trails;

/// <summary>
/// Spatial hash over way samples, for "is this point inside a way's corridor" (tree clearing) and
/// "nearest point on any way" (snapping). Results do not depend on hash iteration order.
/// </summary>
internal sealed class CorridorIndex
{
    private const int BucketCm = 800;

    private readonly Dictionary<long, List<(int Entry, int Sample)>> _buckets = [];
    private readonly List<(int WayId, WayGeometry Geometry, long HalfWidthCm)> _entries = [];

    public void Add(int wayId, WayGeometry geometry, int corridorWidthCm)
    {
        int entry = _entries.Count;
        _entries.Add((wayId, geometry, corridorWidthCm / 2));
        for (int i = 0; i < geometry.SampleCount; i++)
        {
            long key = Key(geometry.Xs[i] / BucketCm, geometry.Zs[i] / BucketCm);
            if (!_buckets.TryGetValue(key, out var list))
                _buckets[key] = list = [];
            list.Add((entry, i));
        }
    }

    public bool IsEmpty => _entries.Count == 0;

    /// <summary>True if the point lies within half the corridor width of any sample of any way.</summary>
    public bool Contains(int xCm, int zCm)
    {
        int bx = xCm / BucketCm, bz = zCm / BucketCm;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (!_buckets.TryGetValue(Key(bx + dx, bz + dz), out var list)) continue;
                foreach (var (entry, sample) in list)
                {
                    var (_, geometry, half) = _entries[entry];
                    long ex = geometry.Xs[sample] - xCm, ez = geometry.Zs[sample] - zCm;
                    if (ex * ex + ez * ez <= half * half) return true;
                }
            }
        return false;
    }

    /// <summary>The closest way sample within the radius (ties: lower way id, then lower sample index).</summary>
    public (int WayId, long DistanceCm, PointCm Point)? Nearest(int xCm, int zCm, int radiusCm)
    {
        int reach = radiusCm / BucketCm + 1;
        int bx = xCm / BucketCm, bz = zCm / BucketCm;
        long bestD2 = (long)radiusCm * radiusCm + 1;
        (int WayId, int Sample, WayGeometry Geometry)? best = null;
        for (int dz = -reach; dz <= reach; dz++)
            for (int dx = -reach; dx <= reach; dx++)
            {
                if (!_buckets.TryGetValue(Key(bx + dx, bz + dz), out var list)) continue;
                foreach (var (entry, sample) in list)
                {
                    var (wayId, geometry, _) = _entries[entry];
                    long ex = geometry.Xs[sample] - xCm, ez = geometry.Zs[sample] - zCm;
                    long d2 = ex * ex + ez * ez;
                    bool better = d2 < bestD2
                        || (d2 == bestD2 && best is { } b && (wayId < b.WayId || (wayId == b.WayId && sample < b.Sample)));
                    if (!better) continue;
                    bestD2 = d2;
                    best = (wayId, sample, geometry);
                }
            }

        if (best is not { } hit) return null;
        var g = hit.Geometry;
        return (hit.WayId, g.Distances[hit.Sample], new PointCm(g.Xs[hit.Sample], g.Zs[hit.Sample]));
    }

    private static long Key(int bx, int bz) => ((long)bx << 32) ^ (uint)bz;
}
