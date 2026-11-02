using Bikepark.Sim.Commands;
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

    private static TrailFeatureType Drop => new()
    {
        Id = "flat_drop", Name = "Flat drop", Kind = FeatureKind.FlatDrop, Material = FeatureMaterial.Wood, Difficulty = 760, CrossesRock = RockFormation.RockFace,
        MaxSpanMeters = 15, MaxRockDropMeters = 3, RampMeters = 40, LandingDeckMeters = 6, MinGradient = -100, RequiredLevel = 7, Wood = 20, WoodPerMeter = 1, WorkMinutes = 240, WorkPerMeter = 6,
    };

    private static TrailFeatureType Roller => new()
    {
        Id = "wood_roller", Name = "Wooden roller", Kind = FeatureKind.WoodRoller, Material = FeatureMaterial.Wood, Difficulty = 520, CrossesRock = RockFormation.MediumRock,
        MaxSpanMeters = 20, MaxRockDropMeters = 3, MinGradient = -40, RequiredLevel = 3, Wood = 5, WoodPerMeter = 2, WorkMinutes = 120, WorkPerMeter = 12,
    };

    private static RockFeatureChoice All => new([Drop], _ => true);

    private static readonly TrailRules Rules = new() { FeatureStartMarginMeters = 5, FeatureEndMarginMeters = 5 };

    private static List<string?> Reasons(TerrainGrid grid, RockFeatureChoice choice)
    {
        var g = Geometry(grid, WayKind.Trail);
        return RockFeatures.Options(grid, g, Rules, choice, Assert.Single(RockFeatures.Crossings(g))).Select(o => o.Reason).ToList();
    }

    [Fact]
    public void MediumRock_TakesADrop_NotARoller_PathsGoAround()
    {
        var grid = Band(RockFormation.MediumRock);
        Assert.All(Reasons(grid, All), r => Assert.Null(r));
        Assert.Single(Reasons(grid, new RockFeatureChoice([Drop, Roller], _ => true))); // an older save's roller type isn't offered
        var g = Geometry(grid, WayKind.Trail);
        var crossing = Assert.Single(RockFeatures.Crossings(g));
        Assert.Contains("doesn't go over rock", RockFeatures.CannotPlace(grid, g, Rules, Roller, new RockFeaturePlacement(Roller.Id, crossing.FromCm - 100, crossing.SpanCm + 200)));
        Assert.True(IsError(grid, WayKind.AccessPath, "mediumRock"));
        Assert.All(Geometry(grid, WayKind.Trail).Rocks, r => Assert.Equal(RockFormation.MediumRock, r.Kind));
    }

    [Fact]
    public void RockFace_TakesADrop_WithinItsLimits()
    {
        var grid = Band(RockFormation.RockFace);
        Assert.True(IsError(grid, WayKind.AccessPath, "rockFace"));

        // No option at all without the types: refused.
        Assert.True(IsError(grid, WayKind.Trail, "rockFace"));

        var narrow = new RockFeatureChoice([new TrailFeatureType { Id = Drop.Id, Name = Drop.Name, Kind = FeatureKind.FlatDrop, CrossesRock = RockFormation.RockFace, MaxSpanMeters = 3, MaxRockDropMeters = 3, RampMeters = 10, LandingDeckMeters = 6 }], _ => true);
        Assert.Contains("wide", Reasons(grid, narrow)[0]);
        var low = new RockFeatureChoice([new TrailFeatureType { Id = Drop.Id, Name = Drop.Name, Kind = FeatureKind.FlatDrop, CrossesRock = RockFormation.RockFace, MaxSpanMeters = 15, MaxRockDropMeters = 1, RampMeters = 10, LandingDeckMeters = 6 }], _ => true);
        Assert.Contains("at most", Reasons(grid, low)[0]);
    }

    [Fact]
    public void Locked_AndBadPlacements_AreRefused()
    {
        var grid = Band(RockFormation.MediumRock);
        var g = Geometry(grid, WayKind.Trail);
        Assert.All(Reasons(grid, new RockFeatureChoice([Drop], _ => false)), r => Assert.Contains("level", r));

        var crossing = Assert.Single(RockFeatures.Crossings(g));
        Assert.Contains("must end on top of the rock", RockFeatures.CannotPlace(grid, g, Rules, Drop, new RockFeaturePlacement("flat_drop", crossing.FromCm - 500, 2500, 100)));
        Assert.Contains("landing must start below", RockFeatures.CannotPlace(grid, g, Rules, Drop, RockFeatures.FlatDropAt(grid, g, Drop, crossing, crossing.FromCm, crossing.FromCm + 100)));
        Assert.Contains("needs a flat drop", RockFeatures.CannotCross(grid, g, Rules, All, null));
        var fit = RockFeatures.DefaultPlacement(grid, g, Drop, crossing);
        Assert.Null(RockFeatures.CannotCross(grid, g, Rules, All, [fit]));
    }

    [Fact]
    public void FlatDrop_ShippedLimits_Reach20MetersOfGapAndDrop()
    {
        var shipped = TestWorlds.FeatureCatalog().Single(t => t.Id == "flat_drop");
        Assert.Equal(20, shipped.MaxSpanMeters);
        Assert.Equal(20, shipped.MaxRockDropMeters);

        var grid = Band(RockFormation.MediumRock);
        var g = Geometry(grid, WayKind.Trail);
        var crossing = Assert.Single(RockFeatures.Crossings(g));
        var type = new TrailFeatureType { Id = Drop.Id, Name = Drop.Name, Kind = FeatureKind.FlatDrop, CrossesRock = RockFormation.RockFace, MaxSpanMeters = shipped.MaxSpanMeters, MaxRockDropMeters = shipped.MaxRockDropMeters, RampMeters = 40, LandingDeckMeters = 6 };
        Assert.Null(RockFeatures.CannotPlace(grid, g, Rules, type, RockFeatures.FlatDropAt(grid, g, type, crossing, crossing.ToCm, crossing.ToCm + 2000)));
        Assert.Contains("at most 20 m", RockFeatures.CannotPlace(grid, g, Rules, type, RockFeatures.FlatDropAt(grid, g, type, crossing, crossing.ToCm, crossing.ToCm + 2100)));
    }

    [Fact]
    public void FlatDrop_DeckIsLevelOnTheRock_AndMeetsTheHillside()
    {
        var grid = Band(RockFormation.MediumRock);
        var g = Geometry(grid, WayKind.Trail);
        var crossing = Assert.Single(RockFeatures.Crossings(g));
        var drop = RockFeatures.DefaultPlacement(grid, g, Drop, crossing);
        long lip = drop.StartCm + drop.RampCm;
        Assert.Equal(crossing.ToCm, lip);
        Assert.True(drop.StartCm <= crossing.FromCm);

        // The deck's level height (the ground at its start) is at or above the top of everything it passes over.
        Assert.True(g.HeightAt(drop.StartCm) >= RockFeatures.RockTopCm(grid, g, crossing.FromCm, lip));
        Assert.All(RockFeatures.RocksOn(grid, g, crossing.FromCm, lip), r => Assert.True(r.Kind is ScatterKind.Boulder or ScatterKind.Face));

        // Out of reach of the hillside: refused, with the reason.
        var shortReach = new TrailFeatureType { Id = Drop.Id, Name = Drop.Name, Kind = FeatureKind.FlatDrop, CrossesRock = RockFormation.RockFace, MaxSpanMeters = 15, MaxRockDropMeters = 30, RampMeters = 0, LandingDeckMeters = 6 };
        Assert.Contains("too high", RockFeatures.CannotPlace(grid, g, Rules, shortReach, RockFeatures.DefaultPlacement(grid, g, shortReach, crossing)));
    }

    [Fact]
    public void Features_SetTheRating_CostGrowsWithLength_TrailClosedUntilBuilt()
    {
        var grid = Band(RockFormation.MediumRock);
        var g = Geometry(grid, WayKind.Trail);
        var crossing = Assert.Single(RockFeatures.Crossings(g));
        var placement = RockFeatures.DefaultPlacement(grid, g, Drop, crossing);
        var feature = new TrailFeature { Id = 2, TypeId = "flat_drop", DistanceCm = placement.StartCm, LengthCm = placement.LengthCm, RampCm = placement.RampCm, Built = false };
        var trail = new Way { Id = 1, Kind = WayKind.Trail, Built = true, Points = [M(10, 60), M(90, 60)], Features = [feature] };
        var rules = new TrailRules();
        Assert.True(WayNetwork.Build([trail], [], [], grid, rules, [Drop]).IsBlockedByRock(trail));
        feature.Built = true;
        var network = WayNetwork.Build([trail], [], [], grid, rules, [Drop]);
        Assert.False(network.IsBlockedByRock(trail));
        Assert.Equal(TrailRating.Black, network.Geometry(1).Rating);
        Assert.True(WorkCosts.FeatureWood(Drop, 2000) > WorkCosts.FeatureWood(Drop, 1000));
        Assert.True(WorkCosts.FeatureMinutes(Drop, 2000) > WorkCosts.FeatureMinutes(Drop, 1000));
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
        Assert.DoesNotContain(Codes(grid, WayKind.Trail, scenario: true), c => c is "rockFace" or "rockTooBig" or "rockCrossing" or "stoneField");

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

    [Fact]
    public void BuildWayCommand_KeepsItsRockFeatures_ThroughJson()
    {
        ICommand command = new BuildWayCommand(WayKind.Trail, "T", [M(1, 1), M(2, 2)], RockFeatures: [new RockFeaturePlacement("flat_drop", 2000, 1800)]);
        var back = System.Text.Json.JsonSerializer.Deserialize<ICommand>(System.Text.Json.JsonSerializer.Serialize(command, Persistence.SimJson.Indented), Persistence.SimJson.Indented);
        Assert.Equal(command, back);
        Assert.NotEqual(command, ((BuildWayCommand)command) with { RockFeatures = null });
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
