using Bikepark.Sim.Commands;
using Bikepark.Sim.Events;
using Bikepark.Sim.Safety;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>Running costs that grow with the park: insurance per feature.</summary>
public class UpkeepTests
{
    private const int FlowCountry = 11, RedRocket = 12; // demo_lift_network.json on the Demo

    private static Simulation World(Action<ScenarioDefinition>? tweak = null)
    {
        var scenario = TestWorlds.LiftScenario();
        tweak?.Invoke(scenario);
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        foreach (var c in TestWorlds.DemoLiftNetwork())
            sim.Commands.Enqueue(c.Command, 0);
        sim.Step();
        return sim;
    }

    [Fact]
    public void Insurance_GrowsWithEveryBuiltFeature_ByItsRating()
    {
        var sim = World(s => s.CrashRules.InsurancePerFeatureByRating = new() { Blue = 100, Red = 400, Black = 1200 });
        var rules = sim.State.CrashRules;
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "double", 30_000, Instant: true)); // black
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "drop", 10_000, Instant: true)); // red
        sim.Step();
        var counts = TrailFeatures.BuiltByRating(sim.Network);
        Assert.Equal([0, 0, 1, 1], counts);
        Assert.Equal(CrashMath.Premium(rules, sim.State.Safety) + 400 + 1200, CrashMath.Premium(rules, sim.State.Safety, counts));
    }
}
