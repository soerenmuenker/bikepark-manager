using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class TerrainSculptTests
{
    private static TerrainGrid Flat() => TestTerrain.Grid((_, _) => 100_000, size: 64);

    private static TerrainStroke Stroke(SculptTool tool, int radiusM, int heightCm, params (int X, int Z)[] pointsM) => new()
    {
        Tool = tool,
        RadiusCm = radiusM * 100,
        HeightCm = heightCm,
        Points = pointsM.Select(p => new PointCm(p.X * 100, p.Z * 100)).ToList(),
    };

    [Fact]
    public void Raise_LiftsTheCentreByItsHeight_AndFadesSmoothlyToTheEdge()
    {
        var grid = TerrainSculptor.Apply(Flat(), [Stroke(SculptTool.Raise, 10, 500, (32, 32))]);

        Assert.Equal(100_500, grid.HeightAtSample(32, 32));
        Assert.Equal(100_000, grid.HeightAtSample(42, 32)); // at the radius: untouched
        Assert.Equal(100_000, grid.HeightAtSample(50, 32));
        int previous = int.MaxValue;
        for (int x = 32; x <= 42; x++)
        {
            int h = grid.HeightAtSample(x, 32);
            Assert.True(h <= previous, "falls monotonically from the centre");
            previous = h;
        }
        // Smoothstep: flat at the centre and at the edge, no step anywhere (well below the 5 m a 1 m step would be).
        Assert.True(grid.HeightAtSample(32, 32) - grid.HeightAtSample(33, 32) < 20);
        Assert.True(grid.HeightAtSample(41, 32) - grid.HeightAtSample(42, 32) < 20);
        Assert.True(grid.SlopeAtSample(37, 32) < 1000);
    }

    [Fact]
    public void Lower_DigsByItsHeight()
    {
        var grid = TerrainSculptor.Apply(Flat(), [Stroke(SculptTool.Lower, 8, 300, (20, 20))]);

        Assert.Equal(99_700, grid.HeightAtSample(20, 20));
        Assert.Equal(100_000, grid.HeightAtSample(30, 20));
    }

    [Fact]
    public void AStroke_TakesTheStrongestEffectAlongItsPath_SoPointSpacingDoesNotMatter()
    {
        var sparse = TerrainSculptor.Apply(Flat(), [Stroke(SculptTool.Raise, 6, 400, (10, 30), (50, 30))]);
        var dense = TerrainSculptor.Apply(Flat(), [Stroke(SculptTool.Raise, 6, 400, (10, 30), (20, 30), (35, 30), (50, 30))]);

        Assert.Equal(sparse.ComputeHash(), dense.ComputeHash());
        Assert.Equal(100_400, sparse.HeightAtSample(30, 30)); // an even ridge along the path
        Assert.Equal(100_400, sparse.HeightAtSample(45, 30));
    }

    [Fact]
    public void Strokes_AddUp()
    {
        var once = Stroke(SculptTool.Raise, 10, 300, (32, 32));
        var grid = TerrainSculptor.Apply(Flat(), [once, once]);

        Assert.Equal(100_600, grid.HeightAtSample(32, 32));
    }

    [Theory]
    [InlineData(TerrainSurface.Forest)]
    [InlineData(TerrainSurface.Roots)]
    [InlineData(TerrainSurface.Rock)]
    [InlineData(TerrainSurface.Grass)]
    public void Paint_SetsTheSurfaceInTheBrush_AndKeepsTheHeights(TerrainSurface surface)
    {
        var start = TestTerrain.Grid((_, _) => 100_000, trees: (_, _) => 120, size: 64); // forest everywhere
        var stroke = new TerrainStroke { Tool = SculptTool.Paint, Surface = surface, RadiusCm = 1000, Points = [new PointCm(3200, 3200)] };
        var grid = TerrainSculptor.Apply(start, [stroke]);

        Assert.Equal(surface, grid.SurfaceAtSample(32, 32));
        Assert.Equal(surface, grid.SurfaceAtSample(37, 32)); // full strength in the inner 60 %
        Assert.Equal(TerrainSurface.Forest, grid.SurfaceAtSample(50, 32));
        Assert.Equal(start.Heights.ToArray(), grid.Heights.ToArray());
    }

    [Fact]
    public void Validate_RejectsBadStrokes()
    {
        Assert.NotEmpty(Stroke(SculptTool.Raise, 0, 100, (1, 1)).Validate(0));
        Assert.NotEmpty(Stroke(SculptTool.Raise, 10, 0, (1, 1)).Validate(0));
        Assert.NotEmpty(Stroke(SculptTool.Raise, 10, 100).Validate(0));
        Assert.NotEmpty(new TerrainStroke { Tool = SculptTool.Paint, Surface = TerrainSurface.Water, RadiusCm = 500, Points = [new PointCm(0, 0)] }.Validate(0));
        Assert.Empty(new TerrainStroke { Tool = SculptTool.Paint, Surface = TerrainSurface.Rock, RadiusCm = 500, Points = [new PointCm(0, 0)] }.Validate(0));
    }

    [Fact]
    public void ScenarioSculpt_ShapesTheSimTerrain_AndSurvivesASave()
    {
        var scenario = TestWorlds.Scenario();
        scenario.TerrainSculpt = [Stroke(SculptTool.Raise, 20, 1_000, (300, 700))];
        var state = ScenarioLoader.CreateWorld(scenario);
        var sim = new Simulation(state);
        var plain = new Simulation(TestWorlds.Create());

        Assert.Equal(plain.Terrain.HeightAtSample(300, 700) + 1_000, sim.Terrain.HeightAtSample(300, 700));
        Assert.Equal(sim.Terrain.ComputeHash(), sim.BaseTerrain.ComputeHash()); // no pads yet

        var loaded = new Simulation(SaveGame.Deserialize(SaveGame.Serialize(state)));
        Assert.Equal(sim.Terrain.ComputeHash(), loaded.Terrain.ComputeHash());
    }
}
