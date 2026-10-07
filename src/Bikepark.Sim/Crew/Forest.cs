using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Crew;

/// <summary>
/// Which scatter trees stand, are gone or are claimed by a job. Trees are derived from the terrain
/// (<see cref="TerrainScatter"/>) and identified by their position; the sim and the view both use this, so the wood a
/// job yields and the trees the player sees always agree.
/// <list type="bullet">
///   <item>gone: in the corridor of a built way or lift line, or in <see cref="WorldState.FelledTrees"/>;</item>
///   <item>claimed: listed in a job's <see cref="Job.Trees"/> (felled ones are also in the felled list).</item>
/// </list>
/// </summary>
public static class Forest
{
    /// <summary>Trees that are felled or that a job will fell: a new job must not count them again.</summary>
    public static HashSet<PointCm> Taken(WorldState state)
    {
        var taken = new HashSet<PointCm>(state.FelledTrees);
        foreach (var job in state.Jobs)
            taken.UnionWith(job.Trees);
        return taken;
    }

    /// <summary>True if the tree at (x, z) has been cut down (for drawing).</summary>
    public static Func<int, int, bool> GoneFilter(WayNetwork network, WorldState state)
    {
        var felled = new HashSet<PointCm>(state.FelledTrees);
        return (x, z) => felled.Contains(new PointCm(x, z)) || (!network.IsEmpty && network.IsInCorridor(x, z));
    }

    /// <summary>Standing, unclaimed trees within the circle, nearest to the centre first (ties: x, then z).</summary>
    public static List<PointCm> TreesInCircle(TerrainGrid grid, WayNetwork network, IReadOnlySet<PointCm> taken, PointCm center, int radiusCm)
    {
        long r2 = (long)radiusCm * radiusCm;
        var found = new List<(long D2, PointCm Tree)>();
        foreach (var tree in TreesInBox(grid, center.X - radiusCm, center.Z - radiusCm, center.X + radiusCm, center.Z + radiusCm))
        {
            long dx = tree.X - center.X, dz = tree.Z - center.Z;
            long d2 = dx * dx + dz * dz;
            if (d2 > r2 || taken.Contains(tree) || network.IsInCorridor(tree.X, tree.Z)) continue;
            found.Add((d2, tree));
        }
        return found.OrderBy(f => f.D2).ThenBy(f => f.Tree.X).ThenBy(f => f.Tree.Z).Select(f => f.Tree).ToList();
    }

    /// <summary>
    /// Standing, unclaimed trees in the corridor of a (planned) way, in order along it (ties: x, then z). These have to
    /// be felled before the way can be dug.
    /// </summary>
    public static List<PointCm> TreesAlong(
        TerrainGrid grid, WayNetwork network, IReadOnlySet<PointCm>? taken, WayGeometry geometry, int corridorWidthCm)
    {
        var corridor = new CorridorIndex();
        corridor.Add(0, geometry, corridorWidthCm);
        int half = corridorWidthCm / 2;

        int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
        var xs = geometry.Xs;
        var zs = geometry.Zs;
        for (int i = 0; i < geometry.SampleCount; i++)
        {
            minX = Math.Min(minX, xs[i]);
            maxX = Math.Max(maxX, xs[i]);
            minZ = Math.Min(minZ, zs[i]);
            maxZ = Math.Max(maxZ, zs[i]);
        }

        var found = new List<(long Along, PointCm Tree)>();
        foreach (var tree in TreesInBox(grid, minX - half, minZ - half, maxX + half, maxZ + half))
        {
            if (taken?.Contains(tree) == true || (!network.IsEmpty && network.IsInCorridor(tree.X, tree.Z))) continue;
            if (corridor.Nearest(tree.X, tree.Z, half) is not { } hit) continue;
            found.Add((hit.DistanceCm, tree));
        }
        return found.OrderBy(f => f.Along).ThenBy(f => f.Tree.X).ThenBy(f => f.Tree.Z).Select(f => f.Tree).ToList();
    }

    /// <summary>Scatter trees whose position lies in the box (cm, inclusive).</summary>
    private static IEnumerable<PointCm> TreesInBox(TerrainGrid grid, long minX, long minZ, long maxX, long maxZ)
    {
        // Scatter is collected by cell origin; widen by a cell so trees jittered into the box are found.
        int cell = TerrainScatter.TreeCellMeters;
        var scatter = new List<ScatterInstance>();
        TerrainScatter.Collect(grid,
            (int)Math.Max(0, minX / 100 - cell), (int)Math.Max(0, minZ / 100 - cell),
            (int)(maxX / 100 + 1), (int)(maxZ / 100 + 1), scatter);
        foreach (var item in scatter)
        {
            if (item.Kind != ScatterKind.Tree) continue;
            if (item.XCm < minX || item.XCm > maxX || item.ZCm < minZ || item.ZCm > maxZ) continue;
            yield return new PointCm(item.XCm, item.ZCm);
        }
    }
}
