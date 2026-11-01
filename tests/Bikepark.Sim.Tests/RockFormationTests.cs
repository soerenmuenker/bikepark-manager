using Bikepark.Sim.Crew;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class RockFormationTests
{
    private const int Size = 128;

    private static PointCm M(int x, int z) => new(x * 100, z * 100);

    /// <summary>A gentle plane falling east with a band of the formation across it, 40 to 50 m.</summary>
    private static TerrainGrid Band(RockFormation formation)
    {
        var plane = TestTerrain.Plane(100, Size);
        int n = plane.Samples;
        var layer = new byte[n * n];
        for (int z = 0; z < n; z++)
            for (int x = 40; x <= 50; x++)
                layer[z * n + x] = (byte)formation;
        return new TerrainGrid(plane.Settings with { RockFormations = true }, plane.Seed, plane.Heights.ToArray(), plane.Slopes.ToArray(),
            plane.TreeDensity.ToArray(), plane.Rock.ToArray(), plane.Roots.ToArray(), plane.WaterDepth.ToArray(), null, layer);
    }

    private static WayGeometry Geometry(TerrainGrid grid, WayKind kind, bool ignoreRocks = false) =>
        WayGeometry.Build(grid, kind, [M(10, 60), M(90, 60)], 1000, ignoreRocks: ignoreRocks);

    private static List<string> Codes(TerrainGrid grid, WayKind kind, bool scenario = false)
    {
        var path = new Way { Id = 1, Kind = WayKind.AccessPath, Built = true, Points = [M(5, 5), M(30, 5)] };
        var network = WayNetwork.Build([path], grid, new TrailRules());
        return WayPlanner.Plan(grid, network, new TrailRules(), kind, [M(10, 60), M(90, 60)], mayStandAlone: scenario).Issues.Select(i => i.Code).ToList();
    }

    private static bool IsError(TerrainGrid grid, WayKind kind, string code)
    {
        var path = new Way { Id = 1, Kind = WayKind.AccessPath, Built = true, Points = [M(5, 5), M(30, 5)] };
        var network = WayNetwork.Build([path], grid, new TrailRules());
        return WayPlanner.Plan(grid, network, new TrailRules(), kind, [M(10, 60), M(90, 60)]).Issues.Any(i => i.Code == code && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void SparseStones_AreClearedForWork_RatingStays()
    {
        var plain = Geometry(TestTerrain.Plane(100, Size), WayKind.Trail);
        var trail = Geometry(Band(RockFormation.SparseStones), WayKind.Trail);
        Assert.Equal(plain.Rating, trail.Rating);
        Assert.True(trail.StoneMeters >= 10);
        var crew = new CrewRules();
        var rules = new TrailRules();
        Assert.True(WorkCosts.WayWorkMinutes(crew, rules, WayKind.Trail, trail) > WorkCosts.WayWorkMinutes(crew, rules, WayKind.Trail, plain));
        Assert.Contains("clearStones", Codes(Band(RockFormation.SparseStones), WayKind.Trail));
    }

    [Fact]
    public void DenseStones_MakeTheWholeTrailBlack_PathsClearThem()
    {
        var grid = Band(RockFormation.DenseStones);
        var trail = Geometry(grid, WayKind.Trail);
        Assert.Equal(TrailRating.Black, trail.Rating);
        Assert.Equal(RatingCause.Rock, trail.RatingCause);
        Assert.Equal(0, trail.StoneMeters);
        Assert.Contains("stoneField", Codes(grid, WayKind.Trail));
        Assert.False(IsError(grid, WayKind.Trail, "stoneField"));

        var path = Geometry(grid, WayKind.AccessPath);
        Assert.True(path.StoneMeters >= 10);
        Assert.Contains("clearStones", Codes(grid, WayKind.AccessPath));
    }

    [Fact]
    public void MediumRock_GetsAWoodenRampOnATrail_PathsGoAround()
    {
        var grid = Band(RockFormation.MediumRock);
        var trail = Geometry(grid, WayKind.Trail);
        Assert.Equal(TrailRating.Red, trail.Rating);
        Assert.True(trail.RampMeters >= 10);
        var crew = new CrewRules();
        Assert.Equal(trail.RampMeters * crew.RampWoodPerMeter, WorkCosts.WayWood(crew, trail));
        Assert.Equal(trail.RampMeters * crew.RampWoodPerMeter, WorkCosts.Way(crew, new TrailRules(), WayKind.Trail, trail, 0).WoodNeeded);
        Assert.Contains("woodRamp", Codes(grid, WayKind.Trail));
        Assert.False(IsError(grid, WayKind.Trail, "mediumRock"));
        Assert.True(IsError(grid, WayKind.AccessPath, "mediumRock"));
    }

    [Fact]
    public void RockFace_RefusesEveryWay()
    {
        var grid = Band(RockFormation.RockFace);
        Assert.True(IsError(grid, WayKind.Trail, "rockFace"));
        Assert.True(IsError(grid, WayKind.AccessPath, "rockFace"));
    }

    [Theory]
    [InlineData(RockFormation.DenseStones)]
    [InlineData(RockFormation.MediumRock)]
    [InlineData(RockFormation.RockFace)]
    public void ScenarioWays_IgnoreRocks(RockFormation formation)
    {
        var grid = Band(formation);
        Assert.Empty(Geometry(grid, WayKind.Trail, ignoreRocks: true).Rocks);
        Assert.Equal(Geometry(TestTerrain.Plane(100, Size), WayKind.Trail).Rating, Geometry(grid, WayKind.Trail, ignoreRocks: true).Rating);
        Assert.DoesNotContain(Codes(grid, WayKind.Trail, scenario: true), c => c is "rockFace" or "mediumRock" or "stoneField" or "woodRamp");

        var scenarioWay = new Way { Id = 1, Kind = WayKind.Trail, Origin = WayOrigin.Scenario, Built = true, Points = [M(10, 60), M(90, 60)] };
        Assert.Empty(WayNetwork.Build([scenarioWay], grid, new TrailRules()).Geometry(1).Rocks);
    }

    [Fact]
    public void ClippingARockCorner_DoesNotCount()
    {
        var plane = TestTerrain.Plane(100, Size);
        int n = plane.Samples;
        var layer = new byte[n * n];
        layer[60 * n + 40] = (byte)RockFormation.DenseStones;
        var grid = new TerrainGrid(plane.Settings with { RockFormations = true }, plane.Seed, plane.Heights.ToArray(), plane.Slopes.ToArray(),
            plane.TreeDensity.ToArray(), plane.Rock.ToArray(), plane.Roots.ToArray(), plane.WaterDepth.ToArray(), null, layer);
        Assert.Empty(Geometry(grid, WayKind.Trail).Rocks);
    }

    private static TerrainSettings Generated(bool on) => new()
    {
        Seed = 7, SizeMeters = 400, PeakXMeters = 200, PeakZMeters = 200, PeakRadiusMeters = 300, ReliefCm = 30_000, TreeLineCm = 87_000, RockFormations = on,
    };

    [Fact]
    public void Formations_AreDeterministic_AndOffByDefault()
    {
        var off = TerrainGenerator.Generate(Generated(false), 0);
        Assert.False(off.HasRockFormations);
        Assert.Equal(RockFormation.None, off.RockFormationAt(10_000, 10_000));
        Assert.DoesNotContain(TerrainScatter.CollectAll(off), s => s.Kind is ScatterKind.Stone or ScatterKind.Boulder or ScatterKind.Face);

        var on = TerrainGenerator.Generate(Generated(true), 0);
        Assert.Equal(on.ComputeHash(), TerrainGenerator.Generate(Generated(true), 0).ComputeHash());
        Assert.True(off.Heights.SequenceEqual(on.Heights));
        Assert.True(off.Rock.SequenceEqual(on.Rock));
        Assert.Contains(on.Formations.ToArray(), b => b != 0);
    }

    [Fact]
    public void Trees_AvoidBigRocks()
    {
        var grid = TerrainGenerator.Generate(Generated(true), 0);
        Assert.DoesNotContain(TerrainScatter.CollectAll(grid), s => s.Kind == ScatterKind.Tree && grid.RockFormationAt(s.XCm, s.ZCm) >= RockFormation.MediumRock);
    }

    [Fact]
    public void Pads_ClearFormations()
    {
        var grid = Band(RockFormation.RockFace);
        var pad = new TerrainPad(4500, 6000, 1000, 1000, 1000, 0, grid.HeightAt(4500, 6000), 1000);
        var edited = TerrainEditor.Apply(grid, [new TerrainEdit(1, 1, pad)]);
        Assert.Equal(RockFormation.None, edited.RockFormationAt(4500, 6000));
        Assert.Equal(RockFormation.RockFace, edited.RockFormationAt(4500, 11000));
    }

    [Fact]
    public void PaintedRock_CreatesFormations()
    {
        var grid = TerrainGenerator.Generate(Generated(true), 0);
        var painted = TerrainSculptor.Apply(grid, [new TerrainStroke { Tool = SculptTool.Paint, Surface = TerrainSurface.Rock, RadiusCm = 20_000, Points = [M(200, 200)] }]);
        Assert.True(painted.Formations.ToArray().Count(b => b != 0) > grid.Formations.ToArray().Count(b => b != 0));
    }

    [Fact]
    public void ShippedMaps_KeepTheirBuiltWays()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "Bikepark.sln"))) dir = dir.Parent;
        var sim = new Simulation(ScenarioLoader.CreateWorld(ScenarioLoader.LoadFile(Path.Combine(dir.FullName, "data/scenarios/starter_valley.json"))));
        sim.Step();
        Assert.True(sim.Terrain.HasRockFormations);
        var easyPeasy = sim.State.Ways.Single(w => w.Name == "Easy Peasy");
        Assert.Equal(TrailRating.Blue, sim.Network!.Geometry(easyPeasy.Id).Rating);
    }
}
