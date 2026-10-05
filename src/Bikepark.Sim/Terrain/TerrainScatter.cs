namespace Bikepark.Sim.Terrain;

public enum ScatterKind : byte
{
    Tree = 0,
    Rock = 1,
}

/// <summary>A tree or rock placed on the terrain. Positions in cm; yaw in thousandths of a full turn.</summary>
public readonly record struct ScatterInstance(
    ScatterKind Kind,
    int XCm,
    int ZCm,
    int YCm,
    int ScalePermille,
    int YawPermille,
    byte Variant);

/// <summary>
/// Deterministic placement of trees and rocks from the terrain layers. Uses a jittered global grid with
/// one candidate per cell, so the result for a region does not depend on how the map is split into chunks:
/// an instance belongs to the region containing its cell's origin.
/// </summary>
public static class TerrainScatter
{
    public const int TreeCellMeters = 5;
    public const int RockCellMeters = 8;
    private const uint TreeSalt = 0x7EE5u;
    private const uint RockSalt = 0x60C4u;

    /// <summary>Appends instances whose cell origin lies in [minX, maxX) × [minZ, maxZ) (meters).</summary>
    public static void Collect(TerrainGrid grid, int minXM, int minZM, int maxXM, int maxZM, List<ScatterInstance> output)
    {
        uint seed = (uint)(grid.Seed ^ (grid.Seed >> 32));
        CollectKind(grid, ScatterKind.Tree, TreeCellMeters, seed ^ TreeSalt, minXM, minZM, maxXM, maxZM, output);
        CollectKind(grid, ScatterKind.Rock, RockCellMeters, seed ^ RockSalt, minXM, minZM, maxXM, maxZM, output);
    }

    public static List<ScatterInstance> CollectAll(TerrainGrid grid)
    {
        var all = new List<ScatterInstance>();
        Collect(grid, 0, 0, grid.SizeMeters + 1, grid.SizeMeters + 1, all);
        return all;
    }

    private static void CollectKind(
        TerrainGrid grid, ScatterKind kind, int cell, uint seed,
        int minXM, int minZM, int maxXM, int maxZM, List<ScatterInstance> output)
    {
        int cellCm = cell * 100;
        int limitCm = grid.SizeMeters * 100;
        for (int cz = CeilDiv(Math.Max(0, minZM), cell); cz * cell < maxZM; cz++)
        {
            for (int cx = CeilDiv(Math.Max(0, minXM), cell); cx * cell < maxXM; cx++)
            {
                uint h = IntNoise.Hash(cx, cz, seed);
                int xCm = cx * cellCm + (int)(h % (uint)cellCm);
                int zCm = cz * cellCm + (int)((h >> 16) % (uint)cellCm);
                if (xCm > limitCm || zCm > limitCm) continue;

                var sample = grid.Sample(xCm, zCm);
                if (sample.WaterDepthCm > 0) continue;

                uint h2 = IntNoise.Hash(cx, cz, seed + 1);
                int roll = (int)(h2 & 255);
                int scale;
                if (kind == ScatterKind.Tree)
                {
                    if (sample.Surface == TerrainSurface.Rock || roll >= sample.TreeDensity) continue;
                    scale = 750 + (int)((h2 >> 8) % 550);
                }
                else
                {
                    // Rocks where the rock layer is strong, plus the odd boulder in the forest.
                    bool onRock = roll < sample.Rock * 3 / 4;
                    bool boulder = sample.TreeDensity > 0 && (h2 >> 8) % 1000 < 8;
                    if (!onRock && !boulder) continue;
                    scale = 300 + (int)((h2 >> 8) % 400) + sample.Rock * 3;
                }

                output.Add(new ScatterInstance(
                    kind, xCm, zCm, grid.HeightAt(xCm, zCm),
                    scale, (int)((h2 >> 18) % 1000), (byte)(h2 >> 30)));
            }
        }
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
