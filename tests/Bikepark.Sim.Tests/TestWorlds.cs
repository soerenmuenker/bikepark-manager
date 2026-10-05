using Bikepark.Sim.Commands;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;

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
    };

    public static WorldState Create(ulong seed = 1337) => ScenarioLoader.CreateWorld(Scenario(seed));

    /// <summary>A fixed command script used by determinism tests.</summary>
    public static IReadOnlyList<TimedCommand> Script() =>
    [
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
