using Bikepark.Sim.Persistence;
using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Tests;

/// <summary>The default 1 km terrain, generated once for all tests in <see cref="TerrainTests"/>.</summary>
public sealed class DefaultTerrainFixture
{
    public static readonly TerrainSettings Settings = new() { Seed = 1337 };

    public TerrainGrid Grid { get; } = TerrainGenerator.Generate(Settings, worldSeed: 0);
}

public class TerrainTests(DefaultTerrainFixture fixture) : IClassFixture<DefaultTerrainFixture>
{
    private static readonly TerrainSettings Small = new() { Seed = 7, SizeMeters = 256, PeakXMeters = 140, PeakZMeters = 110, PeakRadiusMeters = 200, ReliefCm = 9_000, TreeLineCm = 87_000 };

    private TerrainGrid Grid => fixture.Grid;

    // ---------------------------------------------------------------- determinism

    [Fact]
    public void SameSettings_ProduceIdenticalTerrain()
    {
        var a = TerrainGenerator.Generate(Small, 0);
        var b = TerrainGenerator.Generate(Small, 0);

        Assert.Equal(a.ComputeHash(), b.ComputeHash());
        Assert.Equal(TerrainGenerator.Generate(DefaultTerrainFixture.Settings, 0).ComputeHash(), Grid.ComputeHash());
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentTerrain()
    {
        var a = TerrainGenerator.Generate(Small with { Seed = 1 }, 0);
        var b = TerrainGenerator.Generate(Small with { Seed = 2 }, 0);

        Assert.NotEqual(a.ComputeHash(), b.ComputeHash());
    }

    [Fact]
    public void NullSeed_FollowsWorldSeed()
    {
        var a = TerrainGenerator.Generate(Small with { Seed = null }, 99);
        var b = TerrainGenerator.Generate(Small with { Seed = 99 }, 12345);

        Assert.Equal(99UL, a.Seed);
        Assert.Equal(a.ComputeHash(), b.ComputeHash());
    }

    // ---------------------------------------------------------------- shape

    [Fact]
    public void Relief_MatchesSettings()
    {
        var s = DefaultTerrainFixture.Settings;
        Assert.Equal(s.BaseElevationCm, Grid.MinHeightCm);
        Assert.Equal(s.BaseElevationCm + s.ReliefCm, Grid.MaxHeightCm);
    }

    [Fact]
    public void HighestPoint_IsNearConfiguredPeak()
    {
        var s = DefaultTerrainFixture.Settings;
        double distance = Math.Sqrt(Math.Pow(Grid.Peak.X - s.PeakXMeters, 2) + Math.Pow(Grid.Peak.Z - s.PeakZMeters, 2));
        Assert.True(distance < s.PeakRadiusMeters * 0.15, $"peak at {Grid.Peak} is {distance:F0} m from the configured peak");
    }

    [Fact]
    public void Terrain_FallsAwayFromPeak()
    {
        // Average height on rings around the peak decreases with distance.
        var s = DefaultTerrainFixture.Settings;
        double previous = double.MaxValue;
        foreach (int radius in new[] { 50, 150, 250, 350 })
        {
            double sum = 0;
            int count = 0;
            for (int a = 0; a < 360; a += 5)
            {
                int x = s.PeakXMeters + (int)(radius * Math.Cos(a * Math.PI / 180));
                int z = s.PeakZMeters + (int)(radius * Math.Sin(a * Math.PI / 180));
                if (x < 0 || z < 0 || x > s.SizeMeters || z > s.SizeMeters) continue;
                sum += Grid.HeightAtSample(x, z);
                count++;
            }
            double mean = sum / count;
            Assert.True(mean < previous, $"mean height at {radius} m ({mean:F0}) not below the inner ring ({previous:F0})");
            previous = mean;
        }
    }

    // ---------------------------------------------------------------- layers

    [Fact]
    public void NoTrees_AboveTreeLine()
    {
        int maxTreeHeight = DefaultTerrainFixture.Settings.TreeLineCm + 1_500; // tree line jitter is ±15 m
        int forested = 0;
        for (int z = 0; z < Grid.Samples; z++)
            for (int x = 0; x < Grid.Samples; x++)
            {
                var s = Grid.SampleAt(x, z);
                if (s.TreeDensity == 0) continue;
                forested++;
                Assert.True(s.HeightCm < maxTreeHeight, $"tree density {s.TreeDensity} at {s.HeightCm} cm ({x},{z})");
            }
        Assert.True(forested > Grid.Samples * Grid.Samples / 4, "less than a quarter of the map has trees");
    }

    [Fact]
    public void AllLandSurfaceTypes_Occur_AndWaterLayerIsEmpty()
    {
        var seen = new HashSet<TerrainSurface>();
        for (int z = 0; z < Grid.Samples; z += 2)
            for (int x = 0; x < Grid.Samples; x += 2)
                seen.Add(Grid.SurfaceAtSample(x, z));
        Assert.Equal(new HashSet<TerrainSurface> { TerrainSurface.Grass, TerrainSurface.Forest, TerrainSurface.Roots, TerrainSurface.Rock }, seen);
        Assert.True(Grid.WaterDepth.IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void Rock_IsWhereItIsSteepOrHigh()
    {
        long rockSlope = 0, rockCount = 0, allSlope = 0, all = 0;
        for (int z = 0; z < Grid.Samples; z += 3)
            for (int x = 0; x < Grid.Samples; x += 3)
            {
                var s = Grid.SampleAt(x, z);
                allSlope += s.SlopePermille;
                all++;
                if (s.Surface != TerrainSurface.Rock) continue;
                rockSlope += s.SlopePermille;
                rockCount++;
            }
        Assert.True(rockCount > 0);
        Assert.True(rockSlope / rockCount > allSlope / all, "rock is not steeper than average");
    }

    // ---------------------------------------------------------------- queries

    [Fact]
    public void Queries_InterpolateBetweenSamples()
    {
        var grid = Synthetic((x, z) => x * 50 + z * 20); // 5% grade east, 2% south
        Assert.Equal(grid.HeightAtSample(3, 4), grid.HeightAt(300, 400));
        Assert.Equal(3 * 50 + 4 * 20 + 25 + 10, grid.HeightAt(350, 450));
        Assert.Equal(538, grid.SlopeAt(350, 450)); // sqrt(50² + 20²) = 53.85 cm per m = 53.85% grade
    }

    [Fact]
    public void Queries_ClampOutsideTheMap()
    {
        var grid = Synthetic((x, z) => x * 10 + z);
        Assert.Equal(grid.HeightAtSample(0, 0), grid.HeightAt(-500, -500));
        Assert.Equal(grid.HeightAtSample(grid.SizeMeters, grid.SizeMeters), grid.HeightAt(1_000_000, 1_000_000));
        Assert.Equal(grid.HeightAtSample(grid.SizeMeters, 0), grid.HeightAt(grid.SizeMeters * 100L, 0));
        Assert.False(grid.Contains(-1, 0));
        Assert.True(grid.Contains(grid.SizeMeters * 100L, 0));
    }

    [Fact]
    public void Slope_OfAPlane_MatchesItsGrade()
    {
        var grid = Synthetic((x, z) => 100_000 + x * 100); // 100 cm per m = 45°
        Assert.Equal(1000, grid.SlopeAtSample(10, 10));
        Assert.Equal(1000, grid.SlopeAtSample(0, 10)); // one-sided difference at the edge
    }

    [Fact]
    public void Surface_FollowsLayerPriority()
    {
        var grid = Synthetic((_, _) => 0,
            trees: (x, _) => x == 1 ? (byte)200 : x == 2 ? (byte)200 : (byte)0,
            roots: (x, _) => x == 2 ? (byte)200 : (byte)0,
            rock: (x, _) => x == 3 ? (byte)200 : (byte)0,
            water: (x, _) => x == 4 ? (byte)50 : (byte)0);
        Assert.Equal(TerrainSurface.Grass, grid.SurfaceAt(0, 0));
        Assert.Equal(TerrainSurface.Forest, grid.SurfaceAt(100, 0));
        Assert.Equal(TerrainSurface.Roots, grid.SurfaceAt(200, 0));
        Assert.Equal(TerrainSurface.Rock, grid.SurfaceAt(300, 0));
        Assert.Equal(TerrainSurface.Water, grid.SurfaceAt(449, 0)); // nearest sample
    }

    // ---------------------------------------------------------------- scatter

    [Fact]
    public void Scatter_IsIndependentOfChunking()
    {
        var all = TerrainScatter.CollectAll(Grid);
        var chunked = new List<ScatterInstance>();
        for (int z = 0; z <= Grid.SizeMeters; z += 64)
            for (int x = 0; x <= Grid.SizeMeters; x += 64)
                TerrainScatter.Collect(Grid, x, z, x + 64, z + 64, chunked);

        Assert.True(all.Count > 1000);
        Assert.Equal(all.ToHashSet(), chunked.ToHashSet());
        Assert.Equal(all.Count, chunked.Count);
    }

    [Fact]
    public void Scatter_RespectsLayers()
    {
        foreach (var item in TerrainScatter.CollectAll(Grid))
        {
            var s = Grid.Sample(item.XCm, item.ZCm);
            Assert.Equal(0, s.WaterDepthCm);
            Assert.Equal(Grid.HeightAt(item.XCm, item.ZCm), item.YCm);
            if (item.Kind == ScatterKind.Tree)
                Assert.True(s.TreeDensity > 0, $"tree at ({item.XCm},{item.ZCm}) without forest");
        }
    }

    // ---------------------------------------------------------------- simulation integration

    [Fact]
    public void Terrain_IsDerivedFromWorldState_AndSurvivesSaveLoad()
    {
        var scenario = TestWorlds.Scenario(seed: 5);
        scenario.Terrain = Small with { Seed = null };
        var state = Scenarios.ScenarioLoader.CreateWorld(scenario);
        Assert.Equal(5UL, state.Terrain.Seed); // resolved at scenario creation

        var original = new Simulation(state).Terrain;
        var loaded = new Simulation(SaveGame.Deserialize(SaveGame.Serialize(state))).Terrain;

        Assert.Equal(original.ComputeHash(), loaded.ComputeHash());
        Assert.DoesNotContain("heights", SaveGame.Serialize(state), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidSettings_AreRejected()
    {
        Assert.Throws<InvalidDataException>(() => TerrainGenerator.Generate(Small with { PeakXMeters = 5000 }, 0));
        Assert.Throws<InvalidDataException>(() => TerrainGenerator.Generate(Small with { SizeMeters = 4 }, 0));

        var scenario = TestWorlds.Scenario();
        scenario.Terrain = new TerrainSettings { RidgeCount = 99 };
        Assert.Throws<InvalidDataException>(() => Scenarios.ScenarioLoader.CreateWorld(scenario));
    }

    private static TerrainGrid Synthetic(
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
