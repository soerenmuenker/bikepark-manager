using Bikepark.Sim.Commands;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class SceneryTests
{
    private const int Samples = 61, Cell = 5, Size = 300;

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bikepark.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, relative);
    }

    private static PointCm M(int x, int z) => new(x * 100, z * 100);

    private static TerrainScenery Scenery() => new()
    {
        // Falls to the south (+z); a brook runs down the middle, a lake in the south west, a house in the north east.
        Streams = [new SceneryStream { Points = [M(150, 5), M(150, 295)], WidthCm = 150 }],
        Lakes = [new SceneryLake { Outline = [M(20, 200), M(90, 200), M(90, 270), M(20, 270)] }],
        Buildings = [new SceneryBuilding { CenterX = 22000, CenterZ = 6000, HalfLengthCm = 800, HalfWidthCm = 500, HeightCm = 700 }],
        Streets = [new SceneryStreet { Points = [M(180, 150), M(290, 150)], WidthCm = 500 }],
    };

    private static TerrainSettings Settings(TerrainScenery? scenery)
    {
        var heights = new byte[Samples * Samples * 2];
        var forest = new byte[Samples * Samples];
        var rock = new byte[Samples * Samples];
        Array.Fill(forest, (byte)255);
        for (int z = 0; z < Samples; z++)
            for (int x = 0; x < Samples; x++)
            {
                // 1 m in 10 down to the south, bumpy enough that a stream must not simply follow it
                int dm = 500 - z * 5 + (z % 7 == 0 ? 8 : 0);
                int i = z * Samples + x;
                heights[2 * i] = (byte)dm;
                heights[2 * i + 1] = (byte)(dm >> 8);
            }
        return new TerrainSettings
        {
            Seed = 5,
            SizeMeters = Size,
            RoughnessPermille = 0,
            TreeLineCm = 900_000,
            Heightmap = new TerrainHeightmap
            {
                CellMeters = Cell,
                Samples = Samples,
                MinElevationCm = 70_000,
                HeightsDm = TerrainHeightmap.Pack(heights),
                Forest = TerrainHeightmap.Pack(forest),
                Rock = TerrainHeightmap.Pack(rock),
                Scenery = scenery,
            },
        };
    }

    private static TerrainGrid Grid() => TerrainGenerator.Generate(Settings(Scenery()), 0);

    private static Simulation Sim()
    {
        var scenario = ScenarioLoader.LoadFile(RepoFile("data/scenarios/farrenpoint.json"));
        scenario.Terrain = Settings(Scenery());
        scenario.Parcels = [];
        scenario.Commands = [new TimedCommand(0, new BuildWayCommand(WayKind.AccessPath, "", [M(120, 40), M(120, 120)], WayOrigin.Scenario))];
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        sim.Step();
        return sim;
    }

    [Fact]
    public void Stream_NeverRisesDownstream_AndFillsWater()
    {
        var grid = Grid();
        var stream = Assert.Single(grid.Scenery!.Streams);
        for (int i = 1; i < stream.Points.Count; i++)
            Assert.True(stream.Points[i].LevelCm <= stream.Points[i - 1].LevelCm);
        Assert.True(stream.Points[0].Z < stream.Points[^1].Z);
        foreach (var p in stream.Points.Where((_, i) => i % 10 == 0))
        {
            var s = grid.Sample(p.X, p.Z);
            Assert.True(s.WaterDepthCm > 0);
            Assert.Equal(p.LevelCm - s.WaterDepthCm, s.HeightCm);
        }
    }

    [Fact]
    public void Stream_MappedAgainstTheFlow_RunsDownhillAnyway()
    {
        var scenery = Scenery();
        scenery.Streams[0].Points.Reverse();
        var stream = TerrainGenerator.Generate(Settings(scenery), 0).Scenery!.Streams[0];
        Assert.True(stream.Points[0].LevelCm > stream.Points[^1].LevelCm);
    }

    [Fact]
    public void Lake_IsFlatAtItsLowestShore_AndBlocks()
    {
        var grid = Grid();
        var lake = Assert.Single(grid.Scenery!.Lakes);
        var low = TerrainGenerator.Generate(Settings(null), 0);
        Assert.InRange(lake.LevelCm, low.HeightAt(5500, 27000) - 100, low.HeightAt(5500, 27000) + 100);
        foreach (var (x, z) in new[] { (30, 210), (80, 260), (55, 235) })
        {
            var s = grid.Sample(x * 100, z * 100);
            Assert.Equal(lake.LevelCm - s.WaterDepthCm, s.HeightCm);
            Assert.Equal(TerrainSceneryData.Lake, grid.BlockedAt(x * 100, z * 100));
        }
        Assert.Equal(0, grid.BlockedAt(15000 + 3000, 23000));
    }

    [Fact]
    public void Lake_IslandStaysDry()
    {
        var scenery = Scenery();
        scenery.Lakes[0].Holes = [[M(50, 230), M(60, 230), M(60, 240), M(50, 240)]];
        var grid = TerrainGenerator.Generate(Settings(scenery), 0);
        Assert.Equal(0, grid.BlockedAt(5500, 23500));
        Assert.Equal(0, grid.Sample(5500, 23500).WaterDepthCm);
    }

    [Fact]
    public void BuildingsStreetsAndWater_HaveNoTrees_TheForestAroundDoes()
    {
        var grid = Grid();
        Assert.Equal(0, grid.Sample(22000, 6000).TreeDensity);
        Assert.Equal(TerrainSceneryData.Building, grid.BlockedAt(22000, 6000));
        Assert.Equal(0, grid.Sample(25000, 15000).TreeDensity);
        Assert.Equal(0, grid.Sample(15000, 15000).TreeDensity);
        Assert.Equal(0, TerrainScatter.CollectAll(grid).Count(t => t.Kind == ScatterKind.Tree && t.XCm is >= 21500 and <= 22500 && t.ZCm is >= 5700 and <= 6300));
        Assert.True(TerrainScatter.CollectAll(grid).Count(t => t.Kind == ScatterKind.Tree) > 100);
    }

    [Fact]
    public void WaysAcrossLakesAndBuildings_AreRefused_AcrossBrooksAllowed()
    {
        var sim = Sim();
        var grid = sim.Terrain;
        var network = sim.Network!;
        var rules = sim.State.TrailRules;

        Assert.Contains(WayPlanner.Plan(grid, network, rules, WayKind.AccessPath, [M(120, 120), M(60, 235)]).Issues, i => i.Code == "lake");
        Assert.Contains(WayPlanner.Plan(grid, network, rules, WayKind.AccessPath, [M(120, 60), M(220, 60)]).Issues, i => i.Code == "building");
        var plan = WayPlanner.Plan(grid, network, rules, WayKind.AccessPath, [M(120, 100), M(190, 100)]);
        Assert.True(plan.IsValid, plan.FirstError);
        Assert.Single(plan.Geometry!.Bridges);
        Assert.InRange(plan.Geometry.BridgeMeters, 1, 6);
    }

    [Fact]
    public void ScenarioTracks_MayTouchLakes()
    {
        var sim = Sim();
        var plan = WayPlanner.Plan(sim.Terrain, sim.Network!, sim.State.TrailRules, WayKind.AccessPath, [M(120, 120), M(60, 235)], mayStandAlone: true);
        Assert.DoesNotContain(plan.Issues, i => i.Code == "lake");
    }

    [Fact]
    public void ScenarioTracks_MayBeSteeperThanPlayersMayBuild()
    {
        var settings = Settings(null) with { ReliefScalePermille = 3000 };
        var grid = TerrainGenerator.Generate(settings with { Heightmap = settings.Heightmap! with { MinElevationCm = 70_000 } }, 0);
        var rules = new TrailRules { PathMaxGradient = 10, PathGradingMeters = 1 };
        var network = WayNetwork.Build([], grid, rules);
        List<PointCm> straightDown = [M(50, 50), M(50, 250)];

        Assert.Contains(WayPlanner.Plan(grid, network, rules, WayKind.AccessPath, straightDown).Issues, i => i.Code == "pathTooSteep");
        Assert.DoesNotContain(WayPlanner.Plan(grid, network, rules, WayKind.AccessPath, straightDown, mayStandAlone: true).Issues, i => i.Code == "pathTooSteep");
    }

    [Fact]
    public void GeneratedMaps_HaveNoBridges()
    {
        var grid = TerrainGenerator.Generate(new TerrainSettings { Seed = 7, SizeMeters = 256, PeakXMeters = 140, PeakZMeters = 110, PeakRadiusMeters = 200, ReliefCm = 9_000, TreeLineCm = 87_000 }, 0);
        Assert.Null(grid.Scenery);
        Assert.Empty(WayGeometry.Build(grid, WayKind.AccessPath, [M(10, 10), M(100, 100)], 1000).Bridges);
        Assert.Equal(0, grid.BlockedAt(1000, 1000));
    }

    private static Job BridgeJob(Simulation sim)
    {
        sim.Commands.Enqueue(new BuildWayCommand(WayKind.AccessPath, "", [M(120, 100), M(190, 100)]));
        sim.Step();
        var job = Assert.Single(sim.State.Jobs);
        Assert.True(job.Wood > 0);
        return job;
    }

    private static void Open(Simulation sim, int minutes)
    {
        while (!ParkSchedule.IsOpen(sim.State, sim.State.Tick)) sim.Step();
        for (int i = 0; i < minutes; i++) sim.Step();
    }

    [Fact]
    public void Bridge_WaitsForWood_ThenTakesIt_CancelGivesItBack()
    {
        var sim = Sim();
        sim.State.WoodStock = 0;
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Step();
        var job = BridgeJob(sim);
        job.Trees.Clear(); // nothing to fell, so nothing gives wood
        Open(sim, 30);
        Assert.False(job.WoodTaken);
        Assert.Equal(0, job.Progress);
        Assert.False(sim.State.Ways.Last().Built);

        sim.State.WoodStock = job.Wood + 1;
        Open(sim, 5);
        Assert.True(job.WoodTaken);
        Assert.Equal(1, sim.State.WoodStock);

        sim.Commands.Enqueue(new CancelJobCommand(job.Id));
        sim.Step();
        Assert.Equal(job.Wood + 1, sim.State.WoodStock);
    }

    [Fact]
    public void Bridge_FellingGoesFirst_ItsWoodCanPayForTheBridge()
    {
        var sim = Sim();
        sim.State.WoodStock = 0;
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Step();
        var job = BridgeJob(sim);
        Assert.NotEmpty(job.Trees);
        Open(sim, 5);
        Assert.False(job.WoodTaken);
        Assert.True(sim.State.Crew[0].JobId == job.Id);
    }

    [Fact]
    public void PadOnWaterOrBuilding_IsRefused()
    {
        var sim = Sim();
        var issues = (int x, int z) => StructurePlanner.PlanParking(sim.Terrain, sim.Network!, sim.State, M(x, z), M(x + 10, z), 20, scenario: true).Issues;
        Assert.Contains(issues(150, 200), i => i.Code == "onWater");
        Assert.Contains(issues(55, 235), i => i.Code == "onWater");
        Assert.Contains(issues(220, 60), i => i.Code == "onBuilding");
    }

    [Fact]
    public void Scenery_IsDeterministic_AndSurvivesASave()
    {
        var sim = Sim();
        string hash = sim.Terrain.ComputeHash();
        Assert.Equal(TerrainGenerator.Generate(Settings(Scenery()), 0).Scenery!.Blocked.Length, sim.Terrain.Scenery!.Blocked.Length);
        for (int t = 0; t < 200; t++) sim.Step();

        var path = Path.GetTempFileName();
        try
        {
            SaveGame.SaveAtomic(sim.State, path);
            var loaded = new Simulation(SaveGame.Load(path));
            Assert.Equal(hash, loaded.Terrain.ComputeHash());
            Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded.State));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReliefScale_Default_ChangesNothing()
    {
        Assert.Equal(Grid().ComputeHash(), TerrainGenerator.Generate(Settings(Scenery()) with { ReliefScalePermille = 1000 }, 0).ComputeHash());
    }

    [Fact]
    public void ReliefScale_ScalesHeightAboveTheLowestPoint()
    {
        var real = TerrainGenerator.Generate(Settings(null), 0);
        var tall = TerrainGenerator.Generate(Settings(null) with { ReliefScalePermille = 2000 }, 0);

        Assert.Equal(real.MinHeightCm, tall.MinHeightCm);
        foreach (var (x, z) in new[] { (0, 0), (50, 50), (100, 200), (150, 295) })
            Assert.Equal(real.MinHeightCm + 2 * (real.SampleAt(x, z).HeightCm - real.MinHeightCm), tall.SampleAt(x, z).HeightCm);
    }

    [Fact]
    public void ReliefScale_KeepsLakesFlatAndStreamsFalling()
    {
        var grid = TerrainGenerator.Generate(Settings(Scenery()) with { ReliefScalePermille = 2000 }, 0);
        var lake = Assert.Single(grid.Scenery!.Lakes);
        foreach (var (x, z) in new[] { (30, 210), (80, 260) })
        {
            var s = grid.Sample(x * 100, z * 100);
            Assert.Equal(lake.LevelCm - s.WaterDepthCm, s.HeightCm);
        }
        var stream = grid.Scenery.Streams[0];
        for (int i = 1; i < stream.Points.Count; i++)
            Assert.True(stream.Points[i].LevelCm <= stream.Points[i - 1].LevelCm);
    }

    [Fact]
    public void ReliefScale_OutOfRange_IsRefused()
    {
        Assert.Contains((Settings(null) with { ReliefScalePermille = 100 }).Validate(), e => e.Contains("reliefScalePermille"));
    }

    [Fact]
    public void Tarmac_IsKeptWhenAWayIsCutInTwo()
    {
        var sim = Sim();
        sim.Commands.Enqueue(new BuildWayCommand(WayKind.AccessPath, "", [M(120, 120), M(120, 190)], WayOrigin.Scenario, Surface: WaySurface.Tarmac));
        sim.Step();
        Assert.Equal(WaySurface.Tarmac, sim.State.Ways.Last().Surface);
        Assert.Equal(WaySurface.Gravel, sim.State.Ways[0].Surface);
    }

    [Fact]
    public void TarmacRoad_IsPlayerBuilt_ForTheCrew_AndTrailsStartAndEndOnIt()
    {
        var sim = Sim();
        var roads = new[] { (M(200, 40), M(200, 90)), (M(200, 200), M(200, 250)) };
        foreach (var (a, b) in roads)
            sim.Commands.Enqueue(new BuildWayCommand(WayKind.AccessPath, "", [a, b], WayOrigin.Scenario, Surface: WaySurface.Tarmac));
        sim.Step();
        var top = sim.State.Ways[^2];
        var bottom = sim.State.Ways[^1];

        sim.Commands.Enqueue(new BuildWayCommand(WayKind.Trail, "Down", [M(200, 80), M(190, 140), M(200, 210)], Instant: true));
        sim.Step();
        var trail = sim.State.Ways[^1];
        Assert.Equal(WayKind.Trail, trail.Kind);
        Assert.Equal(top.Id, trail.StartJoin?.WayId);
        Assert.Equal(bottom.Id, trail.EndJoin?.WayId);

        sim.Commands.Enqueue(new BuildWayCommand(WayKind.AccessPath, "", [M(200, 250), M(240, 260)], Surface: WaySurface.Tarmac));
        sim.Step();
        var planned = sim.State.Ways[^1];
        Assert.False(planned.Built);
        Assert.Equal(WaySurface.Tarmac, planned.Surface);
        Assert.Equal("a tarmac road", planned.Label);
    }

    [Fact]
    public void ScenarioParking_NeedsNoLift_AndIsWhereGuestsArrive()
    {
        var sim = Sim();
        sim.Commands.Enqueue(new BuildParkingLotCommand("Starter", M(60, 60), M(120, 60), 40, WayOrigin.Scenario));
        sim.Commands.Enqueue(new BuildWayCommand(WayKind.AccessPath, "", [M(60, 60), M(120, 60)], WayOrigin.Scenario));
        sim.Step();
        Assert.Equal(sim.State.ParkingLots.Single().Id, sim.Network!.BaseHub?.Id);
        Assert.True(sim.Network.HasBase);

        sim.Commands.Enqueue(new BuildParkingLotCommand("Mine", M(60, 120), M(120, 120), 40));
        var rejected = new List<CommandRejected>();
        sim.Events.Subscribe<CommandRejected>(rejected.Add);
        sim.Step();
        sim.Events.Dispatch();
        Assert.Contains(rejected, r => r.Reason.Contains("valley station"));
    }

    [Theory]
    [InlineData("auer_berg_2")]
    [InlineData("auer_berg_hocheck")]
    public void RealMaps_BuildTheirRoadsAsWays_NoneAreScenery(string map)
    {
        var rejected = new List<CommandRejected>();
        var sim = new Simulation(ScenarioLoader.CreateWorld(ScenarioLoader.LoadFile(RepoFile($"data/scenarios/{map}.json"))));
        sim.Events.Subscribe<CommandRejected>(rejected.Add);
        sim.Step();
        sim.Events.Dispatch();
        Assert.DoesNotContain(rejected, r => r.Command is BuildWayCommand);
        Assert.Contains(sim.State.Ways, w => w.Surface == WaySurface.Tarmac);
        Assert.Empty(sim.State.Terrain.Heightmap!.Scenery!.Streets);
    }
}
