using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Bikepark.Sim.Terrain;

public enum TerrainSurface : byte
{
    Grass = 0,
    Forest = 1,
    Roots = 2,
    Rock = 3,
    Water = 4,
}

public readonly record struct GridPoint(int X, int Z);

/// <summary>Everything known about the terrain at one point.</summary>
public readonly record struct TerrainSample(
    int HeightCm,
    int SlopePermille,
    byte TreeDensity,
    byte Rock,
    byte Roots,
    byte WaterDepthCm,
    TerrainSurface Surface);

/// <summary>
/// Immutable heightmap with layers, sampled every 1 m. Sample (x, z) sits at world position (x m, z m);
/// +X is east, +Z is south. Point queries take centimeter coordinates and clamp to the map.
/// Slopes are grades in permille (rise / run * 1000; 1000 = 45°).
/// </summary>
public sealed class TerrainGrid
{
    public const int WaterSurfaceThresholdCm = 10;
    public const int RockSurfaceThreshold = 128;
    public const int RootsSurfaceThreshold = 150;
    public const int ForestSurfaceThreshold = 96;

    private readonly int[] _heights;
    private readonly ushort[] _slopes;
    private readonly byte[] _trees;
    private readonly byte[] _rock;
    private readonly byte[] _roots;
    private readonly byte[] _water;

    internal TerrainGrid(
        TerrainSettings settings, ulong seed, int[] heights, ushort[] slopes,
        byte[] trees, byte[] rock, byte[] roots, byte[] water)
    {
        Settings = settings with { };
        Seed = seed;
        SizeMeters = settings.SizeMeters;
        Samples = SizeMeters + 1;
        int count = Samples * Samples;
        if (heights.Length != count || slopes.Length != count || trees.Length != count ||
            rock.Length != count || roots.Length != count || water.Length != count)
            throw new ArgumentException("Layer sizes do not match the grid size.");

        _heights = heights;
        _slopes = slopes;
        _trees = trees;
        _rock = rock;
        _roots = roots;
        _water = water;

        int min = int.MaxValue, max = int.MinValue, peak = 0;
        for (int i = 0; i < count; i++)
        {
            if (heights[i] < min) min = heights[i];
            if (heights[i] > max) { max = heights[i]; peak = i; }
        }
        MinHeightCm = min;
        MaxHeightCm = max;
        Peak = new GridPoint(peak % Samples, peak / Samples);
    }

    public TerrainSettings Settings { get; }
    public ulong Seed { get; }
    public int SizeMeters { get; }

    /// <summary>Samples per side (SizeMeters + 1).</summary>
    public int Samples { get; }

    public int MinHeightCm { get; }
    public int MaxHeightCm { get; }

    /// <summary>Highest sample.</summary>
    public GridPoint Peak { get; }

    public ReadOnlySpan<int> Heights => _heights;
    public ReadOnlySpan<ushort> Slopes => _slopes;
    public ReadOnlySpan<byte> TreeDensity => _trees;
    public ReadOnlySpan<byte> Rock => _rock;
    public ReadOnlySpan<byte> Roots => _roots;
    /// <summary>Water depth in cm. Reserved for later features (ponds, streams); the generator leaves it empty.</summary>
    public ReadOnlySpan<byte> WaterDepth => _water;

    public int Index(int x, int z) => z * Samples + x;

    // ---- Per-sample queries (indices are clamped) ----

    public int HeightAtSample(int x, int z) => _heights[ClampedIndex(x, z)];

    public int SlopeAtSample(int x, int z) => _slopes[ClampedIndex(x, z)];

    public TerrainSurface SurfaceAtSample(int x, int z) => Classify(ClampedIndex(x, z));

    public TerrainSample SampleAt(int x, int z)
    {
        int i = ClampedIndex(x, z);
        return new TerrainSample(_heights[i], _slopes[i], _trees[i], _rock[i], _roots[i], _water[i], Classify(i));
    }

    // ---- Point queries in centimeters ----

    /// <summary>Bilinearly interpolated height in cm.</summary>
    public int HeightAt(long xCm, long zCm) => Bilinear(_heights, xCm, zCm);

    /// <summary>Bilinearly interpolated slope (grade, permille).</summary>
    public int SlopeAt(long xCm, long zCm) => Bilinear(_slopes, xCm, zCm);

    /// <summary>Surface type of the nearest sample.</summary>
    public TerrainSurface SurfaceAt(long xCm, long zCm) => Classify(NearestIndex(xCm, zCm));

    /// <summary>Interpolated height and slope; layers and surface from the nearest sample.</summary>
    public TerrainSample Sample(long xCm, long zCm)
    {
        int i = NearestIndex(xCm, zCm);
        return new TerrainSample(
            HeightAt(xCm, zCm), SlopeAt(xCm, zCm), _trees[i], _rock[i], _roots[i], _water[i], Classify(i));
    }

    public bool Contains(long xCm, long zCm) =>
        xCm >= 0 && zCm >= 0 && xCm <= SizeMeters * 100L && zCm <= SizeMeters * 100L;

    /// <summary>SHA-256 over all layers, for determinism checks.</summary>
    public string ComputeHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(MemoryMarshal.AsBytes(_heights.AsSpan()));
        hash.AppendData(MemoryMarshal.AsBytes(_slopes.AsSpan()));
        hash.AppendData(_trees);
        hash.AppendData(_rock);
        hash.AppendData(_roots);
        hash.AppendData(_water);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private TerrainSurface Classify(int i)
    {
        if (_water[i] >= WaterSurfaceThresholdCm) return TerrainSurface.Water;
        if (_rock[i] >= RockSurfaceThreshold) return TerrainSurface.Rock;
        if (_roots[i] >= RootsSurfaceThreshold) return TerrainSurface.Roots;
        if (_trees[i] >= ForestSurfaceThreshold) return TerrainSurface.Forest;
        return TerrainSurface.Grass;
    }

    private int ClampedIndex(int x, int z) =>
        Index(Math.Clamp(x, 0, Samples - 1), Math.Clamp(z, 0, Samples - 1));

    private int NearestIndex(long xCm, long zCm) =>
        ClampedIndex((int)((Math.Clamp(xCm, 0, SizeMeters * 100L) + 50) / 100),
                     (int)((Math.Clamp(zCm, 0, SizeMeters * 100L) + 50) / 100));

    private int Bilinear(int[] layer, long xCm, long zCm)
    {
        int i = CellIndex(xCm, zCm, out long fx, out long fz);
        return Interpolate(layer[i], layer[i + 1], layer[i + Samples], layer[i + Samples + 1], fx, fz);
    }

    private int Bilinear(ushort[] layer, long xCm, long zCm)
    {
        int i = CellIndex(xCm, zCm, out long fx, out long fz);
        return Interpolate(layer[i], layer[i + 1], layer[i + Samples], layer[i + Samples + 1], fx, fz);
    }

    private static int Interpolate(long v00, long v10, long v01, long v11, long fx, long fz)
    {
        long top = v00 * (100 - fx) + v10 * fx;
        long bottom = v01 * (100 - fx) + v11 * fx;
        long sum = top * (100 - fz) + bottom * fz;
        // Round half away from zero.
        return (int)(sum >= 0 ? (sum + 5000) / 10000 : -((-sum + 5000) / 10000));
    }

    private int CellIndex(long xCm, long zCm, out long fx, out long fz)
    {
        Cell(xCm, out int ix, out fx);
        Cell(zCm, out int iz, out fz);
        return Index(ix, iz);
    }

    private void Cell(long cm, out int index, out long fraction)
    {
        cm = Math.Clamp(cm, 0, SizeMeters * 100L);
        index = (int)(cm / 100);
        fraction = cm % 100;
        if (index == SizeMeters)
        {
            index = SizeMeters - 1;
            fraction = 100;
        }
    }
}
