namespace Bikepark.Sim.Terrain;

/// <summary>
/// Applies stored <see cref="TerrainEdit"/>s to a generated grid, producing a new immutable grid (the generated one
/// is never mutated). Integer-only and applied in list order, so the result is a pure function of the base grid and
/// the edit list.
/// </summary>
public static class TerrainEditor
{
    public static TerrainGrid Apply(TerrainGrid baseGrid, IReadOnlyList<TerrainEdit> edits)
    {
        if (edits.Count == 0) return baseGrid;

        int n = baseGrid.Samples;
        int[] heights = baseGrid.Heights.ToArray();
        byte[] trees = baseGrid.TreeDensity.ToArray();
        byte[] rock = baseGrid.Rock.ToArray();
        byte[] roots = baseGrid.Roots.ToArray();
        byte[] water = baseGrid.WaterDepth.ToArray();

        foreach (var edit in edits)
            ApplyPad(edit.Pad, n, heights, trees, rock, roots, water);

        ushort[] slopes = TerrainGenerator.ComputeSlopes(heights, n);
        return new TerrainGrid(baseGrid.Settings, baseGrid.Seed, heights, slopes, trees, rock, roots, water);
    }

    /// <summary>
    /// Height difference the embankment may bridge at a horizontal distance from the flat area: the embankment grade
    /// up to <paramref name="bandCm"/>, then steepening quadratically so it always meets the natural ground nearby.
    /// </summary>
    public static long AllowedDifference(long distanceCm, long gradePermille, long bandCm)
    {
        long allowed = distanceCm * gradePermille / 1000;
        long beyond = distanceCm - bandCm;
        if (beyond > 0) allowed += beyond * beyond * gradePermille / 1000 / Math.Max(1, bandCm);
        return allowed;
    }

    /// <summary>
    /// Sample bounds (inclusive, clamped to the map) of the flat area plus an embankment that has to bridge at most
    /// <paramref name="maxDifferenceCm"/>.
    /// </summary>
    public static (int MinX, int MinZ, int MaxX, int MaxZ) Bounds(TerrainPad pad, int sizeMeters, int maxDifferenceCm, long bandCm = 0)
    {
        long reach = 0;
        while (AllowedDifference(reach, pad.EmbankmentPermille, bandCm) < maxDifferenceCm)
            reach += 100;
        reach += (long)Math.Max(pad.HalfLengthCm, pad.HalfWidthCm) * 1415 / 1000 + 100;
        int minX = (int)Math.Clamp((pad.CenterX - reach) / 100, 0, sizeMeters);
        int maxX = (int)Math.Clamp((pad.CenterX + reach + 99) / 100, 0, sizeMeters);
        int minZ = (int)Math.Clamp((pad.CenterZ - reach) / 100, 0, sizeMeters);
        int maxZ = (int)Math.Clamp((pad.CenterZ + reach + 99) / 100, 0, sizeMeters);
        return (minX, minZ, maxX, maxZ);
    }

    private static void ApplyPad(TerrainPad pad, int n, int[] heights, byte[] trees, byte[] rock, byte[] roots, byte[] water)
    {
        // Full-grade embankment wide enough for the largest difference under the pad (at least 20 m); beyond that it
        // steepens until it meets the ground, which must happen within the map's relief.
        int maxDiff = 0, min = int.MaxValue, max = int.MinValue;
        var (fx0, fz0, fx1, fz1) = Bounds(pad, n - 1, 0);
        for (int z = fz0; z <= fz1; z++)
            for (int x = fx0; x <= fx1; x++)
                if (pad.Contains(x * 100L, z * 100L))
                    maxDiff = Math.Max(maxDiff, Math.Abs(heights[z * n + x] - pad.TargetHeightCm));
        foreach (int h in heights)
        {
            min = Math.Min(min, h);
            max = Math.Max(max, h);
        }
        long grade = pad.EmbankmentPermille;
        long band = Math.Max(2_000, (long)maxDiff * 1000 / grade);
        int relief = Math.Max(Math.Abs(max - pad.TargetHeightCm), Math.Abs(pad.TargetHeightCm - min));

        var (x0, z0, x1, z1) = Bounds(pad, n - 1, relief, band);
        for (int z = z0; z <= z1; z++)
        {
            for (int x = x0; x <= x1; x++)
            {
                int i = z * n + x;
                long allowed = AllowedDifference(pad.DistanceOutside(x * 100L, z * 100L), grade, band);
                long diff = heights[i] - pad.TargetHeightCm;
                if (allowed > 0 && Math.Abs(diff) <= allowed)
                    continue; // natural ground already within the embankment: untouched, trees stay
                heights[i] = (int)(pad.TargetHeightCm + Math.Clamp(diff, -allowed, allowed));
                trees[i] = 0;
                rock[i] = 0;
                roots[i] = 0;
                water[i] = 0;
            }
        }
    }
}
