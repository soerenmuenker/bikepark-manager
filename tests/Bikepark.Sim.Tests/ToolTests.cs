using System.Text.Json.Nodes;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;

namespace Bikepark.Sim.Tests;

/// <summary>Tools owned in numbers: one per worker, the best first, worn out with use.</summary>
public class ToolTests
{
    // The test world's demo network: Red Rocket = way 3 (see CrewTests).
    private const int RedRocket = 3;

    /// <summary>Test world (10,000 €, open 08:00–20:00) with the demo network built, after tick 0.</summary>
    private static Simulation World()
    {
        var sim = new Simulation(TestWorlds.Create());
        foreach (var c in TestWorlds.DemoNetwork())
            sim.Commands.Enqueue(c.Command, 0);
        sim.Step();
        sim.Events.Clear();
        return sim;
    }

    [Fact]
    public void EachWorker_UsesOneTool_TheBestFirst_TheRestWorkByHand()
    {
        var sim = World();
        for (int i = 0; i < 3; i++) sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new BuyToolCommand("shovel_set"));
        sim.Commands.Enqueue(new BuyToolCommand("shovel_set"));
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "double", 30_000));
        sim.Step();
        sim.State.Jobs[0].WorkMinutes = 100_000;
        sim.RunTicks(480 + 10 - sim.State.Tick); // 10 minutes of work by 3 workers, 2 shovel sets

        Assert.Equal(10 * (1250 + 1250 + 1000), sim.State.Jobs[0].Progress);
        Assert.All(sim.State.Tools, t => Assert.Equal(10, t.MinutesUsed));
        Assert.Equal(1250 + 1250 + 1000, WorkCosts.CrewSpeedPermille(sim.State, WorkType.Digging, 3));
        Assert.Equal(1250, WorkCosts.SpeedPermille(sim.State, WorkType.Digging));
        Assert.Equal(1000, WorkCosts.SpeedPermille(sim.State, WorkType.Felling));
    }

    [Fact]
    public void ToolsWearOut_AndAreGone()
    {
        var sim = World();
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new BuyToolCommand("shovel_set"));
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(RedRocket, "double", 30_000));
        sim.Step();
        sim.State.Jobs[0].WorkMinutes = 100_000;
        var type = sim.State.ToolTypes.Single(t => t.Id == "shovel_set");
        type.LifeMinutes = 20;
        var worn = new List<ToolWornOut>();
        sim.Events.Subscribe<ToolWornOut>(worn.Add);

        sim.RunTicks(480 + 10 - sim.State.Tick);
        Assert.Equal(500, WorkCosts.ConditionPermille(type, sim.State.Tools[0]));
        sim.RunTicks(15);
        sim.Events.Dispatch();
        Assert.Empty(sim.State.Tools);
        Assert.Single(worn);
        Assert.Equal(20 * 1250 + 5 * 1000, sim.State.Jobs[0].Progress); // by hand afterwards
    }

    [Fact]
    public void BetterTools_NeedTheParkLevel()
    {
        var scenario = TestWorlds.Scenario();
        scenario.Rules.UnlockAll = false;
        scenario.StartingMoneyCents = 100_000_000;
        var sim = new Simulation(Scenarios.ScenarioLoader.CreateWorld(scenario));
        sim.Commands.Enqueue(new BuyToolCommand("excavator"));
        sim.Commands.Enqueue(new BuyToolCommand("chainsaw"));
        sim.Step();
        Assert.Contains("needs park level 8", Assert.Single(sim.Events.Pending.OfType<CommandRejected>()).Reason);
        Assert.Equal(["chainsaw"], sim.State.Tools.Select(t => t.TypeId));
    }

    [Fact]
    public void Load_MigratesVersion6_OwnedToolIds_ToTools()
    {
        var state = TestWorlds.Create();
        var root = JsonNode.Parse(SaveGame.Serialize(state))!.AsObject();
        root["version"] = 6;
        var world = root["world"]!.AsObject();
        world.Remove("tools");
        world["ownedToolIds"] = new JsonArray("chainsaw", "shovel_set");
        int next = state.NextEntityId;

        var loaded = SaveGame.Deserialize(root.ToJsonString());

        Assert.Equal(["chainsaw", "shovel_set"], loaded.Tools.Select(t => t.TypeId));
        Assert.Equal([next, next + 1], loaded.Tools.Select(t => t.Id));
        Assert.Equal(next + 2, loaded.NextEntityId);
        Assert.All(loaded.Tools, t => Assert.Equal(0, t.MinutesUsed));
    }
}
