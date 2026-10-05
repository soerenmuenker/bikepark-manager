using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Tests;

/// <summary>Hand-made terrains for tests that need exact, known heights and layers.</summary>
internal static class TestTerrain
{
    /// <summary>A plane falling towards +X by <paramref name="gradePermille"/> (cm per m × 10), 100 m above zero at x = 0.</summary>
    public static TerrainGrid Plane(int gradePermille, int size = 128) =>
        Grid((x, _) => 100_000 - x * gradePermille / 10, size: size);

    public static TerrainGrid Grid(
        Func<int, int, int> height,
        Func<int, int, byte>? trees = null,
        Func<int, int, byte>? roots = null,
        Func<int, int, byte>? rock = null,
        Func<int, int, byte>? water = null,
        int size = 32)
    {
        int n = size + 1;
        var h = new int[n * n];
        var t = new byte[n * n];
        var ro = new byte[n * n];
        var rk = new byte[n * n];
        var w = new byte[n * n];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                int i = z * n + x;
                h[i] = height(x, z);
                t[i] = trees?.Invoke(x, z) ?? 0;
                ro[i] = roots?.Invoke(x, z) ?? 0;
                rk[i] = rock?.Invoke(x, z) ?? 0;
                w[i] = water?.Invoke(x, z) ?? 0;
            }
        var slopes = TerrainGenerator.ComputeSlopes(h, n);
        return new TerrainGrid(new TerrainSettings { SizeMeters = size, PeakXMeters = 0, PeakZMeters = 0 }, 0, h, slopes, t, rk, ro, w);
    }
}
