using Bikepark.Sim.Events;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class HeightmapTests
{
    private const int Samples = 21, Cell = 5;

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bikepark.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, relative);
    }

    /// <summary>A slope rising east with a bump, forest on the west half, rock on the east edge.</summary>
    private static TerrainSettings Settings(int roughness = 0)
    {
        var heights = new byte[Samples * Samples * 2];
        var forest = new byte[Samples * Samples];
        var rock = new byte[Samples * Samples];
        for (int z = 0; z < Samples; z++)
            for (int x = 0; x < Samples; x++)
            {
                int i = z * Samples + x, dm = 50 * x + (x == 10 && z == 10 ? 30 : 0);
                heights[2 * i] = (byte)dm;
                heights[2 * i + 1] = (byte)(dm >> 8);
                forest[i] = (byte)(x < Samples / 2 ? 255 : 0);
                rock[i] = (byte)(x == Samples - 1 ? 255 : 0);
            }
        return new TerrainSettings
        {
            Seed = 5,
            SizeMeters = (Samples - 1) * Cell,
            RoughnessPermille = roughness,
            TreeLineCm = 500_000,
            Heightmap = new TerrainHeightmap
            {
                CellMeters = Cell,
                Samples = Samples,
                MinElevationCm = 70_000,
                HeightsDm = TerrainHeightmap.Pack(heights),
                Forest = TerrainHeightmap.Pack(forest),
                Rock = TerrainHeightmap.Pack(rock),
            },
        };
    }

    [Fact]
    public void Upsampling_KeepsTheRealHeightsAtEverySample()
    {
        var grid = TerrainGenerator.Generate(Settings(), 0);

        Assert.Equal(Samples * Cell - Cell, grid.WidthMeters);
        Assert.Equal(70_000, grid.HeightAt(0, 0));
        Assert.Equal(70_000 + 5 * 500, grid.HeightAt(5 * Cell * 100, 3 * Cell * 100));
        Assert.Equal(70_000 + 10 * 500 + 300, grid.HeightAt(10 * Cell * 100, 10 * Cell * 100));
    }

    [Fact]
    public void Upsampling_IsSmoothBetweenSamples()
    {
        var grid = TerrainGenerator.Generate(Settings(), 0);

        // On the plain slope: 5 m rise per 5 m cell, so 1 m per meter.
        Assert.InRange(grid.HeightAt(2 * Cell * 100 + 200, 3 * Cell * 100), 70_000 + 1_000 + 190, 70_000 + 1_000 + 210);
    }

    [Fact]
    public void LandCover_ComesFromTheMasks()
    {
        var grid = TerrainGenerator.Generate(Settings(), 0);

        Assert.True(grid.Sample(10 * 100, 50 * 100).TreeDensity > 0);
        Assert.Equal(0, grid.Sample(90 * 100, 50 * 100).TreeDensity);
        Assert.Equal(TerrainSurface.Rock, grid.SurfaceAt(100 * 100, 50 * 100));
    }

    [Fact]
    public void SameInput_GivesTheSameTerrain_AndTheSeedOnlyChangesTheDetail()
    {
        var a = TerrainGenerator.Generate(Settings(90), 0);
        var b = TerrainGenerator.Generate(Settings(90), 0);
        var other = TerrainGenerator.Generate(Settings(90) with { Seed = 6 }, 0);

        Assert.Equal(a.ComputeHash(), b.ComputeHash());
        Assert.NotEqual(a.ComputeHash(), other.ComputeHash());
        Assert.InRange(Math.Abs(a.HeightAt(5000, 5000) - other.HeightAt(5000, 5000)), 0, 400);
    }

    [Fact]
    public void Validation_RejectsSizeMismatchAndBadBlobs()
    {
        Assert.Contains((Settings() with { SizeMeters = 99 }).Validate(), e => e.Contains("sizeMeters"));
        Assert.Contains((Settings() with { HeightmapFile = "x.json" }).Validate(), e => e.Contains("heightmapFile"));

        var settings = Settings();
        settings = settings with { Heightmap = settings.Heightmap! with { Forest = "not base64!" } };
        Assert.Throws<InvalidDataException>(() => TerrainGenerator.Generate(settings, 0));
        settings = Settings();
        settings = settings with { Heightmap = settings.Heightmap! with { HeightsDm = TerrainHeightmap.Pack(new byte[3]) } };
        Assert.Throws<InvalidDataException>(() => TerrainGenerator.Generate(settings, 0));
    }

    [Fact]
    public void RealMap_LoadsItsHeightmap_AndGuestsHaveWhereToArrive()
    {
        var sim = new Simulation(ScenarioLoader.CreateWorld(ScenarioLoader.LoadFile(RepoFile("data/scenarios/auer_berg.json"))));
        sim.Step();

        Assert.Null(sim.State.Terrain.HeightmapFile);
        Assert.NotNull(sim.State.Terrain.Heightmap);
        Assert.NotEmpty(sim.State.Ways);
        Assert.All(sim.State.Ways, w => Assert.Equal(WayKind.AccessPath, w.Kind));
        Assert.True(sim.Network!.HasBase);
    }

    [Fact]
    public void ScenarioTracks_MayStandAlone_PlayersPathsMayNot()
    {
        var grid = TerrainGenerator.Generate(new TerrainSettings { Seed = 7, SizeMeters = 256, PeakXMeters = 140, PeakZMeters = 110, PeakRadiusMeters = 200, ReliefCm = 9_000, TreeLineCm = 87_000 }, 0);
        var rules = new TrailRules();
        var first = new Way { Id = 1, Kind = WayKind.AccessPath, Built = true, Points = [new PointCm(1000, 1000), new PointCm(5000, 1000)] };
        var network = WayNetwork.Build([first], grid, rules);
        List<PointCm> apart = [new PointCm(1000, 20000), new PointCm(5000, 20000)];

        Assert.Contains(WayPlanner.Plan(grid, network, rules, WayKind.AccessPath, apart).Issues, i => i.Code == "notConnected");
        Assert.DoesNotContain(WayPlanner.Plan(grid, network, rules, WayKind.AccessPath, apart, mayStandAlone: true).Issues, i => i.Code == "notConnected");
    }

    [Fact]
    public void RealMap_RunsDeterministically()
    {
        static string Run()
        {
            var scenario = ScenarioLoader.LoadFile(RepoFile("data/scenarios/auer_berg.json"));
            var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
            for (int t = 0; t < 2 * 1440; t++) sim.Step();
            return Persistence.StateHash.Compute(sim.State);
        }

        Assert.Equal(Run(), Run());
    }
}
