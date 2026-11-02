using System.IO.Compression;

namespace Bikepark.Sim.Terrain;

/// <summary>
/// A real piece of land as content (<c>data/terrain/*.json</c>, written by <c>tools/terrain-import</c>): a coarse height
/// grid and land cover masks, which <see cref="TerrainGenerator"/> upsamples to the 1 m grid. Lives in
/// <see cref="TerrainSettings"/>, so saves stay self-contained. Grids are row-major, +X east, +Z south; the blobs are
/// base64 of raw deflate.
/// </summary>
public sealed record TerrainHeightmap
{
    /// <summary>Distance between samples in meters.</summary>
    public int CellMeters { get; set; }

    /// <summary>Samples per row (east-west; of a square map: both ways).</summary>
    public int Samples { get; set; }

    /// <summary>Rows (north-south); 0 = square.</summary>
    public int SamplesZ { get; set; }

    /// <summary>How many degrees clockwise from north the map's top edge faces. Only the importer uses it: the map itself is already turned.</summary>
    public int RotationDegrees { get; set; }

    /// <summary>Rows, also of a square map.</summary>
    public int Rows() => SamplesZ > 0 ? SamplesZ : Samples;

    /// <summary>The elevation <see cref="HeightsDm"/> count from.</summary>
    public int MinElevationCm { get; set; }

    /// <summary>Little-endian uint16 per sample: decimeters above <see cref="MinElevationCm"/>.</summary>
    public string HeightsDm { get; set; } = "";

    /// <summary>One byte per sample: 255 = forest.</summary>
    public string Forest { get; set; } = "";

    /// <summary>One byte per sample: 255 = bare rock or scree.</summary>
    public string Rock { get; set; } = "";

    /// <summary>Where it was taken from ("lat,lon" of the centre).</summary>
    public string Origin { get; set; } = "";

    /// <summary>Credit line the data's licences ask for.</summary>
    public string Attribution { get; set; } = "";

    /// <summary>Water, buildings and streets that really stand here, or null.</summary>
    public TerrainScenery? Scenery { get; set; }

    public List<string> Validate(int sizeMeters, int depthMeters)
    {
        var errors = new List<string>();
        if (CellMeters is < 1 or > 30) errors.Add("terrain.heightmap.cellMeters must be within 1..30");
        if (Samples is < 2 or > 1001) errors.Add("terrain.heightmap.samples must be within 2..1001");
        if ((Samples - 1) * CellMeters != sizeMeters) errors.Add("terrain.sizeMeters must equal (heightmap.samples - 1) * cellMeters");
        if (Rows() is < 2 or > 1001) errors.Add("terrain.heightmap.samplesZ must be within 2..1001");
        if ((Rows() - 1) * CellMeters != depthMeters) errors.Add("terrain.depthMeters must equal (heightmap.samplesZ - 1) * cellMeters");
        if (MinElevationCm < 0) errors.Add("terrain.heightmap.minElevationCm must be >= 0");
        if (Scenery is not null) errors.AddRange(Scenery.Validate());
        return errors;
    }

    /// <summary>Heights in cm, one per sample.</summary>
    public int[] DecodeHeights()
    {
        byte[] raw = Inflate(HeightsDm, Samples * Rows() * 2, "heightsDm");
        var heights = new int[Samples * Rows()];
        for (int i = 0; i < heights.Length; i++)
            heights[i] = MinElevationCm + (raw[2 * i] | raw[2 * i + 1] << 8) * 10;
        return heights;
    }

    public byte[] DecodeForest() => Inflate(Forest, Samples * Rows(), "forest");

    public byte[] DecodeRock() => Inflate(Rock, Samples * Rows(), "rock");

    /// <summary>Packs a blob the way the importer does (tests, tools).</summary>
    public static string Pack(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw);
        return Convert.ToBase64String(output.ToArray());
    }

    private static byte[] Inflate(string blob, int length, string name)
    {
        try
        {
            using var input = new MemoryStream(Convert.FromBase64String(blob));
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(length);
            deflate.CopyTo(output);
            if (output.Length != length)
                throw new InvalidDataException($"terrain.heightmap.{name} has {output.Length} bytes, expected {length}");
            return output.ToArray();
        }
        catch (FormatException e)
        {
            throw new InvalidDataException($"terrain.heightmap.{name} is not valid base64", e);
        }
    }
}
