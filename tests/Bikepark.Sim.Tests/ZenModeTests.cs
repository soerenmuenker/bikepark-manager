using System.Text.Json;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>Zen mode (the Demo sandbox: nothing costs money) and the Demo's trails (data/scripts/demo_trails.json).</summary>
public class ZenModeTests
{
    [Fact]
    public void ZenMode_BooksNoSpending_ButKeepsIncome()
    {
        var finance = new FinanceState { MoneyCents = 1_000, ZenMode = true };
        finance.Spend(MoneyCategory.Wages, 4_000);
        finance.Invest(MoneyCategory.Land, 50_000, 42, "Bought the South slope");
        finance.Earn(MoneyCategory.EntryFees, 1_500);

        Assert.Equal(2_500, finance.MoneyCents);
        Assert.Equal(0, finance.ExpensesTodayCents);
        Assert.Equal(0, finance.Today(MoneyCategory.Land));
        Assert.Empty(finance.Transactions);
        Assert.True(finance.CanAfford(10_000_000));
        finance.ZenMode = false;
        Assert.False(finance.CanAfford(10_000_000));
    }

    [Fact]
    public void ZenMode_InTheSandbox_NothingCostsMoney()
    {
        var sim = TestWorlds.RunLift(7, 2 * GameTime.MinutesPerDay, [
            new(0, new SetZenModeCommand(true)),
            .. TestWorlds.DemoLiftNetwork(),
            .. TestWorlds.DemoFacilities(),
            new(10, new HireCrewCommand()),
            new(10, new BuyToolCommand("chainsaw")),
        ], scenario => scenario.StartingMoneyCents = 0);
        var finance = sim.State.Finance;

        Assert.True(finance.ZenMode);
        Assert.Contains(sim.State.Tools, t => t.TypeId == "chainsaw"); // bought with no money at all
        Assert.Equal(0, finance.TotalExpensesCents); // no wages, lift fees or upkeep either
        Assert.True(finance.TotalRevenueCents > 0);
        Assert.Equal(finance.TotalRevenueCents, finance.MoneyCents);
        Assert.Equal(0, finance.DaysInDebt);
    }

    [Fact]
    public void ZenMode_SwitchedOff_CostsComeBack()
    {
        var sim = TestWorlds.RunLift(7, 2 * GameTime.MinutesPerDay, [
            new(0, new SetZenModeCommand(true)),
            .. TestWorlds.DemoLiftNetwork(),
            new(GameTime.MinutesPerDay, new SetZenModeCommand(false)),
        ]);
        Assert.False(sim.State.Finance.ZenMode);
        Assert.True(sim.State.Finance.TotalExpensesCents > 0); // the second day's wages and lift fees
    }

    [Fact]
    public void ZenMode_IsOnlyForSandboxes()
    {
        var scenario = ScenarioLoader.LoadFile(Path.Combine(TestWorlds.RepoRoot(), "data", "scenarios", "starter_valley.json"));
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        var rejections = new List<CommandRejected>();
        sim.Events.Subscribe<CommandRejected>(rejections.Add);
        sim.Commands.Enqueue(new SetZenModeCommand(true), 0);
        sim.Step();
        sim.Events.Dispatch();
        Assert.Single(rejections);
        Assert.False(sim.State.Finance.ZenMode);

        scenario.ZenMode = true; // a career can't start in Zen mode either
        Assert.ThrowsAny<Exception>(() => ScenarioLoader.CreateWorld(scenario));
    }

    [Fact]
    public void Scenario_CanStartInZenMode()
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.ZenMode = true;
        Assert.True(ScenarioLoader.CreateWorld(scenario).Finance.ZenMode);
    }

    [Fact]
    public void DemoTrails_AreBuiltWithoutCrossings_AndGraniteDropsHasFlatDrops()
    {
        // The shipped Demo as the game starts it: rock formations on, the trails, their features and the valley buildings.
        var scenario = ScenarioLoader.LoadFile(Path.Combine(TestWorlds.RepoRoot(), "data", "scenarios", "demo_valley.json"));
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        var rejections = new List<CommandRejected>();
        sim.Events.Subscribe<CommandRejected>(rejections.Add);
        foreach (string file in new[] { "demo_trails.json", "demo_facilities.json" })
            foreach (var c in Script(file)) sim.Commands.Enqueue(c.Command, c.Tick);
        sim.Step();
        foreach (var c in Script("demo_trail_features.json")) sim.Commands.Enqueue(((PlaceTrailFeatureCommand)c.Command) with { Instant = true }, sim.State.Tick);
        sim.Step();
        sim.Events.Dispatch();

        Assert.Empty(rejections);
        Assert.Empty(sim.Network.Crossings); // no way crosses another, the hiking route included
        var trails = sim.State.Ways.Where(w => w.Kind == WayKind.Trail).ToList();
        Assert.Equal(["Flow Country", "Red Rocket", "Granite Drops"], trails.Select(t => t.Name));
        Assert.Equal([TrailRating.Blue, TrailRating.Red, TrailRating.Black], trails.Select(t => sim.Network.Geometry(t.Id).Rating));
        Assert.All(trails, t => Assert.True(sim.Network.IsRideable(t), $"{t.Name} is rideable"));
        Assert.All(trails, t => Assert.All(t.Features, f => Assert.True(f.Built)));

        var granite = trails.Single(t => t.Name == "Granite Drops");
        Assert.Equal(5, granite.Features.Count(f => f.TypeId == "flat_drop"));
        Assert.All(trails.Where(t => t != granite), t => Assert.Empty(RockFeatures.Crossings(sim.Network.Geometry(t.Id)))); // no rock on the others
    }

    private static List<TimedCommand> Script(string file) =>
        JsonSerializer.Deserialize<List<TimedCommand>>(File.ReadAllText(Path.Combine(TestWorlds.RepoRoot(), "data", "scripts", file)), SimJson.Indented)!;
}
