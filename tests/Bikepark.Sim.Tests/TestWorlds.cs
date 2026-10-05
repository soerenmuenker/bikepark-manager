using System.Text.Json;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Tests;

internal static class TestWorlds
{
    public static ScenarioDefinition Scenario(ulong seed = 1337) => new()
    {
        Id = "test",
        Name = "Test",
        Seed = seed,
        ParkName = "Test Park",
        StartingMoneyCents = 1_000_000,
        EntryFeeCents = 1500,
        Rules = new ParkRules(),
        // Same terrain for every world seed (Starter Valley's), so the demo network is valid everywhere.
        Terrain = new TerrainSettings { Seed = 1337 },
    };

    /// <summary>The demo network (gravel path + Blue and Red trail) from data/scripts, built at tick 0.</summary>
    public static IReadOnlyList<TimedCommand> DemoNetwork() =>
        JsonSerializer.Deserialize<List<TimedCommand>>(File.ReadAllText(Path.Combine(RepoRoot(), "data", "scripts", "demo_network.json")), SimJson.Indented)!;

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bikepark.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    public static WorldState Create(ulong seed = 1337) => ScenarioLoader.CreateWorld(Scenario(seed));

    /// <summary>A fixed command script used by determinism tests.</summary>
    public static IReadOnlyList<TimedCommand> Script() =>
    [
        .. DemoNetwork(),
        new(600, new SetEntryFeeCommand(2500)),
        new(1500, new RenameParkCommand("Gravity Hill")),
        new(2000, new SetEntryFeeCommand(99_999)), // rejected: above max
        new(3000, new SetEntryFeeCommand(800)),
        new(3000, new SetEntryFeeCommand(1200)), // same tick: applied after the previous one
    ];

    public static Simulation Run(ulong seed, long days, IEnumerable<TimedCommand>? commands = null)
    {
        var sim = new Simulation(Create(seed));
        foreach (var c in commands ?? [])
            sim.Commands.Enqueue(c.Command, c.Tick);
        sim.RunDays(days);
        return sim;
    }
}
