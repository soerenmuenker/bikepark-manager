using Bikepark.Sim.Commands;
using Bikepark.Sim.Events;
using Bikepark.Sim.Safety;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>Running costs that grow with the park: trail and feature upkeep, insurance per feature.</summary>
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
    public void TrailUpkeep_IsPerKm_PlusEachBuiltFeature()
    {
        var sim = World(s => s.TrailRules.UpkeepCentsPerKm = 10_000);
        long km = sim.Network.Trails.Sum(t => sim.Network.Geometry(t.Id).LengthCm);
        long trails = km * 10_000 / 100_000;
        Assert.Equal(trails, TrailFeatures.DailyUpkeepCents(sim.State, sim.Network));

        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "double", 30_000, Instant: true));
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(FlowCountry, "rollers", 40_000)); // planned: costs nothing yet
        sim.Step();
        long doubleUpkeep = sim.State.TrailFeatureTypes.Single(t => t.Id == "double").UpkeepCentsPerDay;
        Assert.True(doubleUpkeep > 0);
        Assert.Equal(trails + doubleUpkeep, TrailFeatures.DailyUpkeepCents(sim.State, sim.Network));

        // The crew builds the rollers during the day: charged in the evening as well.
        var days = new List<DayReport>();
        sim.Events.Subscribe<DayEnded>(e => days.Add(e.Report));
        sim.RunDays(1);
        sim.Events.Dispatch();
        long evening = TrailFeatures.DailyUpkeepCents(sim.State, sim.Network);
        Assert.Equal(trails + doubleUpkeep + sim.State.TrailFeatureTypes.Single(t => t.Id == "rollers").UpkeepCentsPerDay, evening);
        Assert.Equal(evening, days.Single().TrailUpkeepCents);
        Assert.Equal(evening, sim.State.Finance.TotalTrailUpkeepCents);
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
