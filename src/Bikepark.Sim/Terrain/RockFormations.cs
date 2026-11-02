namespace Bikepark.Sim.Terrain;

/// <summary>What rock stands on a sample, and what a way has to do about it (see <see cref="Trails.WayPlanner"/>).</summary>
public enum RockFormation : byte
{
    None = 0,

    /// <summary>Loose stones: the crew clears them, the line stays simple.</summary>
    SparseStones = 1,

    /// <summary>Too many stones to move: a trail through it is black, a gravel path clears it.</summary>
    DenseStones = 2,

    /// <summary>A trail needs a wooden ramp over it; a gravel path goes around.</summary>
    MediumRock = 3,

    /// <summary>Every way goes around.</summary>
    RockFace = 4,
}

/// <summary>
/// The formation layer, derived from the rock layer and the slopes (<see cref="TerrainSettings.RockFormations"/>). Integer-only
/// and from its own noise, so nothing else about the terrain changes.
/// </summary>
internal static class RockFormations
{
    private const uint FaceSalt = 0x0FACEu, MediumSalt = 0x0B01Du, FieldSalt = 0x0F1E1u, DenseSalt = 0x0DE5Eu;

    // Faces only on grey ground that is very steep.
    private const int FaceMinRock = 128, FaceMinSlope = 1150, FaceGatePermille = 400;

    /// <summary>A smaller patch of face material is only a medium rock (samples = square meters).</summary>
    private const int FaceMinSamples = 150;

    // Rock lies on the grey ground; on grass only the odd thin scatter of stones.
    private const int MediumMinRock = 90, MediumGatePermille = 620;
    private const int FieldMinRock = 60, FieldGatePermille = 450, DenseMinRock = 80, DenseGatePermille = 540;
    private const int GrassStonesMinSlope = 350, GrassStonesGatePermille = 720;

    public static byte[]? Build(TerrainSettings settings, ulong seed, byte[] rock, ushort[] slopes, byte[] water, TerrainSceneryData? scenery, int nx, int nz)
    {
        if (!settings.RockFormations) return null;
        uint s = (uint)(seed ^ (seed >> 32));
        var layer = new byte[nx * nz];
        for (int z = 0; z < nz; z++)
            for (int x = 0; x < nx; x++)
            {
                int i = z * nx + x;
                if (water[i] > 0 || scenery?.Blocked[i] == TerrainSceneryData.Building) continue;
                int r = rock[i], slope = slopes[i];
                if (r >= FaceMinRock && slope >= FaceMinSlope && Above(x, z, 60, 2, s ^ FaceSalt, FaceGatePermille))
                    layer[i] = (byte)RockFormation.RockFace;
                else if (r >= MediumMinRock && Above(x, z, 10, 2, s ^ MediumSalt, MediumGatePermille))
                    layer[i] = (byte)RockFormation.MediumRock;
                else if (r >= FieldMinRock && Above(x, z, 40, 3, s ^ FieldSalt, FieldGatePermille))
                    layer[i] = (byte)(r >= DenseMinRock && Above(x, z, 25, 2, s ^ DenseSalt, DenseGatePermille) ? RockFormation.DenseStones : RockFormation.SparseStones);
                else if (r < FieldMinRock && slope >= GrassStonesMinSlope && Above(x, z, 40, 3, s ^ FieldSalt, GrassStonesGatePermille))
                    layer[i] = (byte)RockFormation.SparseStones;
            }
        ShrinkSmallFaces(layer, nx);
        return layer;
    }

    private static bool Above(int x, int z, int wavelength, int octaves, uint seed, int permille) =>
        IntNoise.Fbm(x, z, wavelength, octaves, seed) > FixedMath.One * permille / 1000;

    private static void ShrinkSmallFaces(byte[] layer, int n)
    {
        var seen = new bool[layer.Length];
        var stack = new Stack<int>();
        var patch = new List<int>();
        for (int start = 0; start < layer.Length; start++)
        {
            if (seen[start] || layer[start] != (byte)RockFormation.RockFace) continue;
            patch.Clear();
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                patch.Add(i);
                int x = i % n;
                if (x > 0) Visit(i - 1);
                if (x + 1 < n) Visit(i + 1);
                if (i >= n) Visit(i - n);
                if (i + n < layer.Length) Visit(i + n);
            }
            if (patch.Count >= FaceMinSamples) continue;
            foreach (int i in patch) layer[i] = (byte)RockFormation.MediumRock;
        }

        void Visit(int i)
        {
            if (seen[i] || layer[i] != (byte)RockFormation.RockFace) return;
            seen[i] = true;
            stack.Push(i);
        }
    }
}
