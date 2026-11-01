namespace Bikepark.Sim.Terrain;

public enum ScatterKind : byte
{
    Tree = 0,
    Rock = 1,
    Stone = 2,
    Boulder = 3,
    Face = 4,
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
    public const int StoneCellMeters = 2;
    public const int BoulderCellMeters = 4;
    public const int FaceCellMeters = 6;
    private const uint StoneSalt = 0x5704u, BoulderSalt = 0xB01Du, FaceSalt = 0xFACEu;

    /// <summary>Appends instances whose cell origin lies in [minX, maxX) × [minZ, maxZ) (meters).</summary>
    public static void Collect(TerrainGrid grid, int minXM, int minZM, int maxXM, int maxZM, List<ScatterInstance> output)
    {
        uint seed = (uint)(grid.Seed ^ (grid.Seed >> 32));
        CollectKind(grid, ScatterKind.Tree, TreeCellMeters, seed ^ TreeSalt, minXM, minZM, maxXM, maxZM, output);
        if (grid.HasRockFormations)
        {
            CollectKind(grid, ScatterKind.Stone, StoneCellMeters, seed ^ StoneSalt, minXM, minZM, maxXM, maxZM, output);
            CollectKind(grid, ScatterKind.Boulder, BoulderCellMeters, seed ^ BoulderSalt, minXM, minZM, maxXM, maxZM, output);
            CollectKind(grid, ScatterKind.Face, FaceCellMeters, seed ^ FaceSalt, minXM, minZM, maxXM, maxZM, output);
        }
        else
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
                    if (grid.RockFormationAt(xCm, zCm) >= RockFormation.MediumRock) continue;
                    scale = 750 + (int)((h2 >> 8) % 550);
                }
                else if (kind != ScatterKind.Rock)
                {
                    var formation = grid.RockFormationAt(xCm, zCm);
                    int more = (int)((h2 >> 8) % 1000);
                    switch (kind)
                    {
                        case ScatterKind.Stone when formation == RockFormation.DenseStones && roll < 230:
                            scale = 300 + Steep(sample, 0, 1200) / 4 + more / 3;
                            break;
                        case ScatterKind.Stone when formation == RockFormation.SparseStones && roll < 50:
                            scale = 250 + Steep(sample, 0, 1200) / 6 + more / 4;
                            break;
                        case ScatterKind.Boulder when formation == RockFormation.MediumRock && roll < 180:
                            scale = 1200 + Steep(sample, 300, 1300) * 2 + more * 13 / 10;
                            break;
                        case ScatterKind.Face when formation == RockFormation.RockFace:
                            scale = 4000 + Steep(sample, 1150, 2000) * 7 + more * 3;
                            break;
                        default:
                            continue;
                    }
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

    /// <summary>How far the slope (permille) is into the range, 0 below it: the steeper, the bigger the rock.</summary>
    private static int Steep(TerrainSample sample, int from, int to) => Math.Clamp(sample.SlopePermille, from, to) - from;

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
