using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.SimRunner.Terrain;

public enum MapMode
{
    Relief,
    Slope,
    Surface,
}

/// <summary>
/// Renders a top-down map of a <see cref="TerrainGrid"/>, one pixel per sample, north up.
/// Tooling only (floating point is fine here; nothing feeds back into the simulation).
/// </summary>
internal static class TerrainMapRenderer
{
    // Same palettes as the Godot debug overlay (game/shaders/terrain.gdshader).
    private static readonly (int MaxPermille, Rgb Color)[] SlopeRamp =
    [
        (141, new Rgb(0.18, 0.55, 0.20)),  // < 8°
        (268, new Rgb(0.55, 0.75, 0.25)),  // < 15°
        (466, new Rgb(0.95, 0.85, 0.25)),  // < 25°
        (700, new Rgb(0.95, 0.55, 0.15)),  // < 35°
        (1000, new Rgb(0.85, 0.20, 0.15)), // < 45°
        (int.MaxValue, new Rgb(0.50, 0.15, 0.55)),
    ];

    private static readonly Rgb[] SurfaceColors =
    [
        new(0.55, 0.75, 0.35), // Grass
        new(0.15, 0.42, 0.18), // Forest
        new(0.55, 0.38, 0.20), // Roots
        new(0.60, 0.60, 0.62), // Rock
        new(0.20, 0.45, 0.85), // Water
    ];

    // Trail colors by rating; same as the Godot way view (game/scripts/ways/WayView.cs).
    private static readonly Rgb[] RatingColors =
    [
        new(0.15, 0.65, 0.25), // Green
        new(0.15, 0.40, 0.90), // Blue
        new(0.90, 0.15, 0.15), // Red
        new(0.08, 0.08, 0.08), // Black
    ];

    public static byte[] Render(TerrainGrid grid, MapMode mode, IReadOnlyList<ScatterInstance>? scatter, WayNetwork? network = null)
    {
        int n = grid.Samples;
        var heights = grid.Heights;
        var rgb = new byte[n * n * 3];

        for (int z = 0; z < n; z++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = grid.Index(x, z);
                var sample = grid.SampleAt(x, z);

                Rgb color = mode switch
                {
                    MapMode.Slope => SlopeColor(sample.SlopePermille),
                    MapMode.Surface => SurfaceColors[(int)sample.Surface],
                    _ => ReliefColor(grid, sample),
                };

                double shade = Hillshade(grid, heights, x, z);
                color = color * (mode == MapMode.Relief ? shade : 0.55 + 0.45 * shade);

                if (mode == MapMode.Relief)
                    color = Contours(grid, heights, x, z, color);

                Put(rgb, i, color);
            }
        }

        if (scatter is not null)
        {
            foreach (var s in scatter)
            {
                int x = s.XCm / 100, z = s.ZCm / 100;
                if (x >= n || z >= n) continue;
                Put(rgb, grid.Index(x, z), s.Kind == ScatterKind.Tree ? new Rgb(0.05, 0.22, 0.08) : new Rgb(0.35, 0.33, 0.32));
            }
        }

        if (network is not null)
            DrawNetwork(rgb, n, network);

        DrawMarker(rgb, n, grid.Peak.X, grid.Peak.Z, new Rgb(0.9, 0.1, 0.1));
        return rgb;
    }

    private static void DrawNetwork(byte[] rgb, int n, WayNetwork network)
    {
        // Paths first (wide, gravel), then trails on top, then junctions and the base.
        foreach (var way in network.Ways.OrderBy(w => w.Kind))
        {
            var g = network.Geometry(way.Id);
            bool path = way.Kind == WayKind.AccessPath;
            var color = path ? new Rgb(0.92, 0.90, 0.84) : RatingColors[(int)g.Rating];
            int radius = path ? 2 : 1;
            for (int i = 0; i < g.SampleCount; i++)
                Disc(rgb, n, g.Xs[i] / 100, g.Zs[i] / 100, radius, color);
        }
        foreach (var way in network.Ways)
        {
            foreach (var join in new[] { way.StartJoin, way.EndJoin })
            {
                if (join is null || !network.TryGetGeometry(join.WayId, out var target)) continue;
                var p = target.PositionAt(join.DistanceCm);
                Disc(rgb, n, p.X / 100, p.Z / 100, 3, new Rgb(1, 1, 1));
                Disc(rgb, n, p.X / 100, p.Z / 100, 2, new Rgb(0.2, 0.2, 0.2));
            }
        }
        if (network.BaseWay is { } baseWay)
        {
            var b = network.Geometry(baseWay.Id).PositionAt(0);
            Disc(rgb, n, b.X / 100, b.Z / 100, 6, new Rgb(1, 1, 1));
            Disc(rgb, n, b.X / 100, b.Z / 100, 4, new Rgb(0.95, 0.55, 0.1));
        }
    }

    private static void Disc(byte[] rgb, int n, int cx, int cz, int radius, Rgb color)
    {
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int x = cx + dx, z = cz + dz;
                if (dx * dx + dz * dz <= radius * radius && x >= 0 && z >= 0 && x < n && z < n)
                    Put(rgb, z * n + x, color);
            }
    }

    private static Rgb ReliefColor(TerrainGrid grid, TerrainSample s)
    {
        if (s.WaterDepthCm >= TerrainGrid.WaterSurfaceThresholdCm)
            return Rgb.Lerp(new Rgb(0.45, 0.65, 0.9), new Rgb(0.15, 0.35, 0.75), Math.Min(1, s.WaterDepthCm / 150.0));

        // Hypsometric tint: valley green → alpine meadow → bare.
        double t = (s.HeightCm - grid.MinHeightCm) / (double)Math.Max(1, grid.MaxHeightCm - grid.MinHeightCm);
        Rgb c = t < 0.6
            ? Rgb.Lerp(new Rgb(0.60, 0.76, 0.42), new Rgb(0.72, 0.74, 0.48), t / 0.6)
            : Rgb.Lerp(new Rgb(0.72, 0.74, 0.48), new Rgb(0.82, 0.80, 0.72), (t - 0.6) / 0.4);

        c = Rgb.Lerp(c, new Rgb(0.20, 0.42, 0.20), s.TreeDensity / 255.0 * 0.85);
        c = Rgb.Lerp(c, new Rgb(0.45, 0.32, 0.18), s.Roots / 255.0 * 0.4);
        c = Rgb.Lerp(c, new Rgb(0.62, 0.61, 0.60), s.Rock / 255.0);
        return c;
    }

    private static Rgb SlopeColor(int permille)
    {
        foreach (var (max, color) in SlopeRamp)
            if (permille < max) return color;
        return SlopeRamp[^1].Color;
    }

    /// <summary>Lambertian shading with light from the north-west, 45° above the horizon.</summary>
    private static double Hillshade(TerrainGrid grid, ReadOnlySpan<int> heights, int x, int z)
    {
        int n = grid.Samples;
        int x0 = Math.Max(0, x - 1), x1 = Math.Min(n - 1, x + 1);
        int z0 = Math.Max(0, z - 1), z1 = Math.Min(n - 1, z + 1);
        double dx = (heights[grid.Index(x1, z)] - heights[grid.Index(x0, z)]) / 100.0 / (x1 - x0);
        double dz = (heights[grid.Index(x, z1)] - heights[grid.Index(x, z0)]) / 100.0 / (z1 - z0);

        // Normal of the surface y = h(x, z): (-dx, 1, -dz).
        double len = Math.Sqrt(dx * dx + 1 + dz * dz);
        double nx = -dx / len, ny = 1 / len, nz = -dz / len;
        const double lx = -0.5, ly = 0.7071, lz = -0.5; // from NW (−x, −z), normalized
        double lambert = Math.Max(0, nx * lx + ny * ly + nz * lz);
        return 0.35 + 0.65 * lambert / ly; // flat ground = 1.0
    }

    private static Rgb Contours(TerrainGrid grid, ReadOnlySpan<int> heights, int x, int z, Rgb color)
    {
        int n = grid.Samples;
        if (x + 1 >= n || z + 1 >= n) return color;
        int h = heights[grid.Index(x, z)];
        foreach (int neighbor in (ReadOnlySpan<int>)[heights[grid.Index(x + 1, z)], heights[grid.Index(x, z + 1)]])
        {
            if (FloorDiv(h, 5_000) != FloorDiv(neighbor, 5_000))
                return color * 0.55;
            if (FloorDiv(h, 1_000) != FloorDiv(neighbor, 1_000))
                return color * 0.82;
        }
        return color;
    }

    private static void DrawMarker(byte[] rgb, int n, int cx, int cz, Rgb color)
    {
        for (int d = -4; d <= 4; d++)
        {
            Plot(cx + d, cz);
            Plot(cx, cz + d);
        }

        void Plot(int x, int z)
        {
            if (x >= 0 && z >= 0 && x < n && z < n)
                Put(rgb, z * n + x, color);
        }
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    private static void Put(byte[] rgb, int i, Rgb c)
    {
        rgb[i * 3] = ToByte(c.R);
        rgb[i * 3 + 1] = ToByte(c.G);
        rgb[i * 3 + 2] = ToByte(c.B);
    }

    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);

    private readonly record struct Rgb(double R, double G, double B)
    {
        public static Rgb operator *(Rgb c, double f) => new(c.R * f, c.G * f, c.B * f);

        public static Rgb Lerp(Rgb a, Rgb b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return new Rgb(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);
        }
    }
}
