using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Terrain;

public enum SculptTool
{
    Raise,
    Lower,
    Paint,
}

/// <summary>
/// One authored brush stroke on the terrain (scenario content, made in tools/scenario-editor). The stroke is a path
/// (one point: a single dab) swept by a round brush of <see cref="RadiusCm"/>. Within a stroke each sample takes the
/// strongest effect along the path, so a dragged stroke is even however its points are spaced; strokes add up in order.
/// Raise/lower move the ground by up to <see cref="HeightCm"/> with a smoothstep falloff to the brush edge; paint
/// blends the surface layers towards <see cref="Surface"/> (full strength in the inner 60 %, fading to the edge).
/// </summary>
public sealed class TerrainStroke
{
    public SculptTool Tool { get; set; }
    public int RadiusCm { get; set; }

    /// <summary>Raise/lower: height change at the centre of the path.</summary>
    public int HeightCm { get; set; }

    /// <summary>Paint: the surface to paint (not water).</summary>
    public TerrainSurface Surface { get; set; }

    public List<PointCm> Points { get; set; } = [];

    public const int MinRadiusCm = 100, MaxRadiusCm = 20_000, MaxHeightCm = 10_000;

    public List<string> Validate(int index)
    {
        var errors = new List<string>();
        string p = $"terrainSculpt[{index}]";
        if (RadiusCm is < MinRadiusCm or > MaxRadiusCm) errors.Add($"{p}.radiusCm must be within {MinRadiusCm}..{MaxRadiusCm}");
        if (Tool != SculptTool.Paint && HeightCm is < 1 or > MaxHeightCm) errors.Add($"{p}.heightCm must be within 1..{MaxHeightCm}");
        if (Tool == SculptTool.Paint && Surface == TerrainSurface.Water) errors.Add($"{p}.surface can't be water");
        if (Points.Count == 0) errors.Add($"{p}.points needs at least one point");
        return errors;
    }
}

/// <summary>
/// Applies <see cref="TerrainStroke"/>s to a generated grid (integer-only, in list order), producing a new immutable
/// grid. Heights change, slopes are recomputed; other layers change only where painted.
/// </summary>
public static class TerrainSculptor
{
    /// <summary>Layer targets of a painted surface: tree density, rock, roots (see the thresholds in <see cref="TerrainGrid"/>).</summary>
    public static (int Trees, int Rock, int Roots) PaintTarget(TerrainSurface surface) => surface switch
    {
        TerrainSurface.Forest => (220, 0, 60),
        TerrainSurface.Roots => (200, 0, 230),
        TerrainSurface.Rock => (0, 255, 0),
        _ => (0, 0, 0),
    };

    public static TerrainGrid Apply(TerrainGrid baseGrid, IReadOnlyList<TerrainStroke> strokes)
    {
        if (strokes.Count == 0) return baseGrid;

        int n = baseGrid.Samples;
        int[] heights = baseGrid.Heights.ToArray();
        byte[] trees = baseGrid.TreeDensity.ToArray();
        byte[] rock = baseGrid.Rock.ToArray();
        byte[] roots = baseGrid.Roots.ToArray();
        byte[] water = baseGrid.WaterDepth.ToArray();

        foreach (var stroke in strokes)
        {
            var (tTrees, tRock, tRoots) = PaintTarget(stroke.Surface);
            ForEachWeight(stroke, n, (i, weight) =>
            {
                switch (stroke.Tool)
                {
                    case SculptTool.Raise: heights[i] += (int)((long)stroke.HeightCm * weight / 1000); break;
                    case SculptTool.Lower: heights[i] -= (int)((long)stroke.HeightCm * weight / 1000); break;
                    default:
                        trees[i] = Blend(trees[i], tTrees, weight);
                        rock[i] = Blend(rock[i], tRock, weight);
                        roots[i] = Blend(roots[i], tRoots, weight);
                        break;
                }
            });
        }

        ushort[] slopes = TerrainGenerator.ComputeSlopes(heights, n);
        return new TerrainGrid(baseGrid.Settings, baseGrid.Seed, heights, slopes, trees, rock, roots, water);
    }

    private static byte Blend(byte value, int target, long weight) => (byte)(value + (target - value) * weight / 1000);

    /// <summary>Calls <paramref name="apply"/> for every sample the stroke reaches with its weight (1..1000, permille).</summary>
    public static void ForEachWeight(TerrainStroke stroke, int samples, Action<int, long> apply)
    {
        long r = stroke.RadiusCm;
        long minX = stroke.Points.Min(p => p.X) - r, maxX = stroke.Points.Max(p => p.X) + r;
        long minZ = stroke.Points.Min(p => p.Z) - r, maxZ = stroke.Points.Max(p => p.Z) + r;
        int x0 = (int)Math.Clamp((minX + 99) / 100, 0, samples - 1), x1 = (int)Math.Clamp(maxX / 100, 0, samples - 1);
        int z0 = (int)Math.Clamp((minZ + 99) / 100, 0, samples - 1), z1 = (int)Math.Clamp(maxZ / 100, 0, samples - 1);
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                long d = DistanceToPath(stroke.Points, x * 100L, z * 100L);
                if (d >= r) continue;
                long weight = Weight(stroke.Tool, d, r);
                if (weight > 0) apply(z * samples + x, weight);
            }
    }

    /// <summary>
    /// Brush weight (permille) at distance <paramref name="d"/> from the path: raise/lower a smoothstep over the whole
    /// radius (no edge, no plateau), paint full in the inner 60 % then a smoothstep to the edge.
    /// </summary>
    public static long Weight(SculptTool tool, long d, long r)
    {
        long t = tool == SculptTool.Paint
            ? Math.Clamp((r - d) * 1000 / Math.Max(1, r * 2 / 5), 0, 1000)
            : Math.Clamp((r - d) * 1000 / r, 0, 1000);
        return t * t * (3000 - 2 * t) / 1_000_000;
    }

    /// <summary>Horizontal distance (cm) from a point to the polyline (a single point: to that point).</summary>
    public static long DistanceToPath(IReadOnlyList<PointCm> points, long x, long z)
    {
        long best = long.MaxValue;
        for (int i = 0; i < points.Count; i++)
        {
            long ax = points[i].X, az = points[i].Z;
            long d;
            if (i + 1 == points.Count)
            {
                if (points.Count > 1) break;
                d = FixedMath.ISqrt((x - ax) * (x - ax) + (z - az) * (z - az));
            }
            else
            {
                long bx = points[i + 1].X, bz = points[i + 1].Z;
                long dx = bx - ax, dz = bz - az, l2 = dx * dx + dz * dz;
                long dot = (x - ax) * dx + (z - az) * dz;
                if (l2 == 0 || dot <= 0) d = FixedMath.ISqrt((x - ax) * (x - ax) + (z - az) * (z - az));
                else if (dot >= l2) d = FixedMath.ISqrt((x - bx) * (x - bx) + (z - bz) * (z - bz));
                else d = Math.Abs((x - ax) * dz - (z - az) * dx) / Math.Max(1, FixedMath.ISqrt(l2));
            }
            best = Math.Min(best, d);
        }
        return best;
    }
}
