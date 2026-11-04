using Bikepark.Sim.Persistence;
using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Tests;

public class DesertBiomeTests
{
    private static TerrainSettings Settings(TerrainBiome biome) => new() { SizeMeters = 300, PeakXMeters = 150, PeakZMeters = 150, PeakRadiusMeters = 200, Biome = biome };

    [Fact]
    public void Alpine_IsTheDefault()
    {
        Assert.Equal(TerrainBiome.Alpine, new TerrainSettings().Biome);
        var unset = new TerrainSettings { SizeMeters = 300, PeakXMeters = 150, PeakZMeters = 150, PeakRadiusMeters = 200 };
        Assert.Equal(TerrainGenerator.Generate(unset, 7).ComputeHash(), TerrainGenerator.Generate(Settings(TerrainBiome.Alpine), 7).ComputeHash());
    }

    [Fact]
    public void Desert_HasSparsePlantsAndNoRoots()
    {
        var grid = TerrainGenerator.Generate(Settings(TerrainBiome.Desert), 7);
        Assert.All(grid.Roots.ToArray(), r => Assert.Equal(0, r));
        Assert.All(grid.TreeDensity.ToArray(), t => Assert.True(t < 96));
        Assert.Contains(grid.TreeDensity.ToArray(), t => t > 0);
    }

    [Fact]
    public void Desert_KeepsTheShapeAndRockOfTheLand()
    {
        var alpine = TerrainGenerator.Generate(Settings(TerrainBiome.Alpine), 7);
        var desert = TerrainGenerator.Generate(Settings(TerrainBiome.Desert), 7);
        Assert.Equal(alpine.Heights.ToArray(), desert.Heights.ToArray());
        Assert.Equal(alpine.Rock.ToArray(), desert.Rock.ToArray());
    }

    [Fact]
    public void Desert_IsDeterministic() =>
        Assert.Equal(TerrainGenerator.Generate(Settings(TerrainBiome.Desert), 7).ComputeHash(), TerrainGenerator.Generate(Settings(TerrainBiome.Desert), 7).ComputeHash());

    [Fact]
    public void Biome_SurvivesASave()
    {
        var world = TestWorlds.Create();
        world.Terrain.Biome = TerrainBiome.Desert;
        Assert.Equal(TerrainBiome.Desert, SaveGame.Deserialize(SaveGame.Serialize(world)).Terrain.Biome);
    }
}
