using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Tests;

/// <summary>A real map may be longer one way than the other: nothing may swap or mix up its two sides.</summary>
public class RectangularMapTests
{
    private const int Columns = 31, Rows = 11, Cell = 5;

    /// <summary>Heights tell the sample apart: 1 m per column, 2 m per row. Forest in the last row only.</summary>
    private static TerrainSettings Settings(int rows = Rows)
    {
        var heights = new byte[Columns * rows * 2];
        var forest = new byte[Columns * rows];
        for (int z = 0; z < rows; z++)
            for (int x = 0; x < Columns; x++)
            {
                int i = z * Columns + x, dm = 10 * x + 20 * z;
                heights[2 * i] = (byte)dm;
                heights[2 * i + 1] = (byte)(dm >> 8);
                forest[i] = (byte)(z == rows - 1 ? 255 : 0);
            }
        return new TerrainSettings
        {
            Seed = 5,
            SizeMeters = (Columns - 1) * Cell,
            DepthMeters = (rows - 1) * Cell,
            RoughnessPermille = 0,
            TreeLineCm = 500_000,
            RockFormations = true,
            Heightmap = new TerrainHeightmap
            {
                CellMeters = Cell,
                Samples = Columns,
                SamplesZ = rows,
                MinElevationCm = 70_000,
                HeightsDm = TerrainHeightmap.Pack(heights),
                Forest = TerrainHeightmap.Pack(forest),
                Rock = TerrainHeightmap.Pack(new byte[Columns * rows]),
            },
        };
    }

    [Fact]
    public void Grid_HasBothSides()
    {
        var grid = TerrainGenerator.Generate(Settings(), 0);

        Assert.Equal(150, grid.WidthMeters);
        Assert.Equal(50, grid.DepthMeters);
        Assert.Equal(151 * 51, grid.Heights.Length);
    }

    [Fact]
    public void Heights_StayAtTheirSamples()
    {
        var grid = TerrainGenerator.Generate(Settings(), 0);

        Assert.Equal(70_000 + 30 * 100, grid.HeightAt(150 * 100, 0));
        Assert.Equal(70_000 + 10 * 200, grid.HeightAt(0, 50 * 100));
        Assert.Equal(70_000 + 7 * 100 + 4 * 200, grid.HeightAt(7 * Cell * 100, 4 * Cell * 100));
    }

    [Fact]
    public void Contains_UsesEachSide()
    {
        var grid = TerrainGenerator.Generate(Settings(), 0);

        Assert.True(grid.Contains(150 * 100, 50 * 100));
        Assert.False(grid.Contains(150 * 100, 51 * 100));
        Assert.False(grid.Contains(151 * 100, 0));
    }

    [Fact]
    public void Queries_ClampToEachSide()
    {
        var grid = TerrainGenerator.Generate(Settings(), 0);

        Assert.Equal(grid.HeightAtSample(150, 50), grid.HeightAt(10_000_000, 10_000_000));
        Assert.Equal(grid.HeightAtSample(150, 50), grid.SampleAt(10_000, 10_000).HeightCm);
    }

    [Fact]
    public void Scatter_StaysOnTheMap()
    {
        var grid = TerrainGenerator.Generate(Settings(), 0);

        var scatter = TerrainScatter.CollectAll(grid);

        Assert.NotEmpty(scatter);
        Assert.All(scatter, s => Assert.True(grid.Contains(s.XCm, s.ZCm)));
        Assert.Contains(scatter, s => s.Kind == ScatterKind.Tree && s.ZCm > 40 * 100);
    }

    [Fact]
    public void Pads_FlattenTheRightSamples()
    {
        var grid = TerrainGenerator.Generate(Settings(), 0);
        var pad = new TerrainPad(120 * 100, 25 * 100, 500, 500, TerrainPad.DirScale, 0, 73_000, 35);

        var edited = TerrainEditor.Apply(grid, [new TerrainEdit(1, 1, pad)]);

        Assert.Equal(73_000, edited.HeightAtSample(120, 25));
        Assert.Equal(grid.HeightAtSample(5, 25), edited.HeightAtSample(5, 25));
    }

    [Fact]
    public void Generated_IsDeterministic() =>
        Assert.Equal(TerrainGenerator.Generate(Settings(), 0).ComputeHash(), TerrainGenerator.Generate(Settings(), 0).ComputeHash());

    [Fact]
    public void Validate_RefusesAWrongDepth()
    {
        var settings = Settings();
        settings.DepthMeters += Cell;

        Assert.Contains(settings.Validate(), e => e.Contains("depthMeters"));
    }

    [Fact]
    public void Square_KeepsItsSettings()
    {
        var settings = new TerrainSettings { SizeMeters = 200, PeakXMeters = 100, PeakZMeters = 100, PeakRadiusMeters = 80 };
        var grid = TerrainGenerator.Generate(settings, 1);

        Assert.Equal(200, grid.DepthMeters);
        Assert.Empty(settings.Validate());
    }
}
