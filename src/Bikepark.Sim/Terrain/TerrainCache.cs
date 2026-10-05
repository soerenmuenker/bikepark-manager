namespace Bikepark.Sim.Terrain;

/// <summary>
/// Remembers the most recently generated terrain, so reloading a save or restarting a scenario with the same
/// settings doesn't regenerate it. Safe because <see cref="TerrainGrid"/> is immutable.
/// </summary>
public static class TerrainCache
{
    private static readonly Lock Gate = new();
    private static (TerrainSettings Settings, ulong Seed, TerrainGrid Grid)? _last;

    public static TerrainGrid Get(TerrainSettings settings, ulong worldSeed)
    {
        lock (Gate)
        {
            if (_last is { } last && last.Settings == settings && last.Seed == worldSeed)
                return last.Grid;

            var grid = TerrainGenerator.Generate(settings, worldSeed);
            _last = (settings with { }, worldSeed, grid);
            return grid;
        }
    }
}
