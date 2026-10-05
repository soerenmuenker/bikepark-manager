namespace Bikepark.Sim.Terrain;

/// <summary>
/// Parameters for <see cref="TerrainGenerator"/>. Stored in <see cref="State.WorldState.Terrain"/>; the generated
/// <see cref="TerrainGrid"/> itself is derived data and is never saved. Heights are absolute elevations in cm.
/// </summary>
public sealed record TerrainSettings
{
    /// <summary>Terrain seed. Null means "use the world seed".</summary>
    public ulong? Seed { get; set; }

    /// <summary>Edge length in meters. The grid has SizeMeters + 1 samples per side at 1 m spacing.</summary>
    public int SizeMeters { get; set; } = 1000;

    /// <summary>Elevation of the lowest point.</summary>
    public int BaseElevationCm { get; set; } = 80_000;

    /// <summary>Height difference between the lowest point and the summit.</summary>
    public int ReliefCm { get; set; } = 35_000;

    public int PeakXMeters { get; set; } = 600;
    public int PeakZMeters { get; set; } = 400;

    /// <summary>Radius of the main mountain's footprint.</summary>
    public int PeakRadiusMeters { get; set; } = 750;

    public int RidgeCount { get; set; } = 3;

    /// <summary>Ridge height relative to the main mountain, in permille.</summary>
    public int RidgeStrengthPermille { get; set; } = 300;

    /// <summary>Strength of small-scale noise, in permille.</summary>
    public int RoughnessPermille { get; set; } = 90;

    /// <summary>Elevation above which no trees grow (with some natural jitter).</summary>
    public int TreeLineCm { get; set; } = 108_000;

    /// <summary>Approximate share of the map inside forest patches, in permille, before elevation/slope limits.</summary>
    public int ForestCoveragePermille { get; set; } = 650;

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (SizeMeters < 32 || SizeMeters > 4000) errors.Add("terrain.sizeMeters must be within 32..4000");
        if (ReliefCm <= 0) errors.Add("terrain.reliefCm must be > 0");
        if (BaseElevationCm < 0) errors.Add("terrain.baseElevationCm must be >= 0");
        if (PeakXMeters < 0 || PeakXMeters > SizeMeters || PeakZMeters < 0 || PeakZMeters > SizeMeters)
            errors.Add("terrain peak must lie inside the map");
        if (PeakRadiusMeters < 50) errors.Add("terrain.peakRadiusMeters must be >= 50");
        if (RidgeCount is < 0 or > 8) errors.Add("terrain.ridgeCount must be within 0..8");
        if (ForestCoveragePermille is < 0 or > 1000) errors.Add("terrain.forestCoveragePermille must be within 0..1000");
        return errors;
    }
}
