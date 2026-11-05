namespace Bikepark.Sim.Terrain;

public enum ScatterKind : byte
{
    Tree = 0,
    Rock = 1,
}

/// <summary>
/// A tree or rock placed on the terrain. Positions in cm; yaw in thousandths of a full turn. Rocks differ only in size and
/// shape; what a rock means for a way comes from the rock itself (<see cref="RockClasses.Of"/>).
/// </summary>
public readonly record struct ScatterInstance(
    ScatterKind Kind,
    int XCm,
    int ZCm,
    int YCm,
    int ScalePermille,
    int YawPermille,
    byte Variant,
    RockShape Shape = RockShape.Slab);

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
    public const int BoulderCellMeters = 6;
    public const int FaceCellMeters = 6;
    private const uint StoneSalt = 0x5704u, BoulderSalt = 0xB01Du, FaceSalt = 0xFACEu;

    /// <summary>Appends instances whose cell origin lies in [minX, maxX) × [minZ, maxZ) (meters).</summary>
    public static void Collect(TerrainGrid grid, int minXM, int minZM, int maxXM, int maxZM, List<ScatterInstance> output) =>
        Collect(grid, minXM, minZM, maxXM, maxZM, output, rocksOnly: false);

    /// <summary>The same, optionally without the trees (what the way rules look at).</summary>
    public static void Collect(TerrainGrid grid, int minXM, int minZM, int maxXM, int maxZM, List<ScatterInstance> output, bool rocksOnly)
    {
        uint seed = (uint)(grid.Seed ^ (grid.Seed >> 32));
        if (!rocksOnly) CollectKind(grid, Layer.Tree, TreeCellMeters, seed ^ TreeSalt, minXM, minZM, maxXM, maxZM, output);
        if (grid.HasRockFormations)
        {
            CollectKind(grid, Layer.Stone, StoneCellMeters, seed ^ StoneSalt, minXM, minZM, maxXM, maxZM, output);
            CollectKind(grid, Layer.Boulder, BoulderCellMeters, seed ^ BoulderSalt, minXM, minZM, maxXM, maxZM, output);
            CollectKind(grid, Layer.Face, FaceCellMeters, seed ^ FaceSalt, minXM, minZM, maxXM, maxZM, output);
        }
        else
            CollectKind(grid, Layer.Plain, RockCellMeters, seed ^ RockSalt, minXM, minZM, maxXM, maxZM, output);
    }

    public static List<ScatterInstance> CollectAll(TerrainGrid grid)
    {
        var all = new List<ScatterInstance>();
        Collect(grid, 0, 0, grid.WidthMeters + 1, grid.DepthMeters + 1, all);
        return all;
    }

    /// <summary>The placement passes, each with its own grid and sizes. All but the first make rocks.</summary>
    private enum Layer { Tree, Plain, Stone, Boulder, Face }

    private static void CollectKind(
        TerrainGrid grid, Layer kind, int cell, uint seed,
        int minXM, int minZM, int maxXM, int maxZM, List<ScatterInstance> output)
    {
        int cellCm = cell * 100;
        int limitXCm = grid.WidthMeters * 100, limitZCm = grid.DepthMeters * 100;
        for (int cz = CeilDiv(Math.Max(0, minZM), cell); cz * cell < maxZM; cz++)
        {
            for (int cx = CeilDiv(Math.Max(0, minXM), cell); cx * cell < maxXM; cx++)
            {
                uint h = IntNoise.Hash(cx, cz, seed);
                int xCm = cx * cellCm + (int)(h % (uint)cellCm);
                int zCm = cz * cellCm + (int)((h >> 16) % (uint)cellCm);
                if (xCm > limitXCm || zCm > limitZCm) continue;

                var sample = grid.Sample(xCm, zCm);
                if (sample.WaterDepthCm > 0) continue;

                uint h2 = IntNoise.Hash(cx, cz, seed + 1);
                int roll = (int)(h2 & 255);
                int scale;
                if (kind == Layer.Tree)
                {
                    if (sample.Surface == TerrainSurface.Rock || roll >= sample.TreeDensity) continue;
                    if (grid.RockFormationAt(xCm, zCm) >= RockFormation.MediumRock) continue;
                    scale = 750 + (int)((h2 >> 8) % 550);
                }
                else if (kind != Layer.Plain)
                {
                    var formation = grid.RockFormationAt(xCm, zCm);
                    int more = (int)((h2 >> 8) % 1000);
                    switch (kind)
                    {
                        // Stones: packed close in a field, a few elsewhere on rocky ground; never more than knee high.
                        case Layer.Stone when formation == RockFormation.DenseStones && roll < 250:
                            scale = 300 + Steep(sample, 0, 1200) / 5 + more / 4;
                            break;
                        case Layer.Stone when formation is RockFormation.SparseStones or RockFormation.MediumRock && roll < 30:
                            scale = 250 + Steep(sample, 0, 1200) / 6 + more / 4;
                            break;
                        case Layer.Boulder when formation == RockFormation.MediumRock && sample.SlopePermille >= BoulderMinSlope && roll < 210:
                        {
                            scale = 1000 + Steep(sample, 300, 1300) / 2 + more / 2;
                            var (yaw, top) = DropBlockPose(grid, xCm, zCm, RockShapes.Size(RockShape.DropBlock).RadiusCm * scale / 1000);
                            int height = RockShapes.Size(RockShape.DropBlock).HeightCm * scale / 1000;
                            output.Add(new ScatterInstance(ScatterKind.Rock, xCm, zCm, top - height, scale, yaw, (byte)(h2 >> 30), RockShape.DropBlock));
                            continue;
                        }
                        case Layer.Face when formation == RockFormation.RockFace:
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

                byte variant = (byte)(h2 >> 30);
                var shape = kind == Layer.Face
                    ? RockShape.Ledged0 + (byte)(variant % RockShapes.LedgedCount)
                    : (RockShape)((h2 >> 23) % RockShapes.LooseCount);
                if (kind == Layer.Stone) scale = Math.Min(scale, RockShapes.StoneMaxScale(shape));
                output.Add(new ScatterInstance(
                    kind == Layer.Tree ? ScatterKind.Tree : ScatterKind.Rock, xCm, zCm, grid.HeightAt(xCm, zCm),
                    scale, (int)((h2 >> 18) % 1000), variant, shape));
            }
        }
    }

    // Sixteen directions, a sixteenth of a turn apart, as the view turns a mesh: +X towards -Z (cos, -sin), × 1000.
    /// <summary>Boulders stand only where a top flush with the hillside still leaves a sheer face of about a meter.</summary>
    public const int BoulderMinSlope = 350;

    private static readonly int[] DirX = [1000, 924, 707, 383, 0, -383, -707, -924, -1000, -924, -707, -383, 0, 383, 707, 924];
    private static readonly int[] DirZ = [0, -383, -707, -924, -1000, -924, -707, -383, 0, 383, 707, 924, 1000, 924, 707, 383];

    /// <summary>
    /// How a boulder stands: turned so its sheer face looks down the fall line, its level top flush with the ground just above
    /// it. Yaw in thousandths of a turn, top in cm.
    /// </summary>
    internal static (int Yaw, int TopCm) DropBlockPose(TerrainGrid grid, int xCm, int zCm, int radiusCm)
    {
        long gx = grid.HeightAt(xCm + 200, zCm) - grid.HeightAt(xCm - 200, zCm);
        long gz = grid.HeightAt(xCm, zCm + 200) - grid.HeightAt(xCm, zCm - 200);
        int best = 0;
        long bestDot = long.MinValue;
        for (int k = 0; k < DirX.Length; k++)
        {
            long dot = -gx * DirX[k] - gz * DirZ[k];
            if (dot > bestDot)
            {
                bestDot = dot;
                best = k;
            }
        }
        int backX = xCm - DirX[best] * radiusCm / 1000, backZ = zCm - DirZ[best] * radiusCm / 1000;
        int top = grid.HeightAt(backX, backZ) + 10;
        return ((best * 1000 + 8) / 16, top);
    }

    /// <summary>How far the slope (permille) is into the range, 0 below it: the steeper, the bigger the rock.</summary>
    private static int Steep(TerrainSample sample, int from, int to) => Math.Clamp(sample.SlopePermille, from, to) - from;

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;
}
