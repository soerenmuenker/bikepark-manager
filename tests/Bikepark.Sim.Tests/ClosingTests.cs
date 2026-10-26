using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>The player closes buildings and lifts (and opens them again): nobody new is served, upkeep drops.</summary>
public class ClosingTests
{
    private static readonly PointCm BarSpot = new(17500, 83000), ShopSpot = new(25000, 90000);

    private static Simulation Demo()
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.StartingMoneyCents = 100_000_000;
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        sim.State.ReputationRules.Enabled = false;
        sim.State.CrashRules.CollisionDistanceCm = 0;
        foreach (var c in TestWorlds.DemoLiftNetwork())
            sim.Commands.Enqueue(c.Command, 0);
        sim.Step();
        sim.Events.Clear();
        return sim;
    }

    private static string? Reject(Simulation sim, ICommand command)
    {
        sim.Events.Clear();
        sim.Commands.Enqueue(command);
        sim.Step();
        return sim.Events.Pending.OfType<CommandRejected>().SingleOrDefault()?.Reason;
    }

    [Fact]
    public void AClosedBar_ServesNobody_AndCostsOnlyPartOfItsUpkeep()
    {
        var sim = Demo();
        Assert.Null(Reject(sim, new BuildFacilityCommand("snack_bar", BarSpot, new PointCm(18500, 83000))));
        var bar = sim.State.Facilities[0];
        var type = FacilityMath.FindType(sim.State, bar.TypeId)!;
        Assert.Null(Reject(sim, new SetFacilityClosedCommand(bar.Id, true)));
        Assert.Contains("already closed", Reject(sim, new SetFacilityClosedCommand(bar.Id, true)));
        Assert.Contains("No such building", Reject(sim, new SetFacilityClosedCommand(99_999, true)));
        Assert.Equal(type.UpkeepPerDayCents * sim.State.Rules.ClosedUpkeepPermille / 1000, FacilityMath.Upkeep(sim.State, type, bar));

        var days = new List<DayReport>();
        sim.Events.Subscribe<DayEnded>(e => days.Add(e.Report));
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.Equal(0, bar.Customers);
        Assert.Equal(0, sim.State.Finance.TotalFoodCents);
        Assert.Equal(type.UpkeepPerDayCents * sim.State.Rules.ClosedUpkeepPermille / 1000, days.Single().FacilityUpkeepCents);

        var changed = new List<FacilityOpenChanged>();
        sim.Events.Subscribe<FacilityOpenChanged>(changed.Add);
        Assert.Null(Reject(sim, new SetFacilityClosedCommand(bar.Id, false)));
        sim.Events.Dispatch();
        Assert.True(Assert.Single(changed).Open);
        sim.RunDays(1);
        Assert.True(bar.Customers > 0);
    }

    [Fact]
    public void AClosedBikeShop_RentsNoBikes()
    {
        var sim = Demo();
        Assert.Null(Reject(sim, new BuildFacilityCommand("bike_shop", ShopSpot, new PointCm(26000, 90000), Level: 2, Bikes: 20)));
        var shop = sim.State.Facilities[0];
        Assert.Null(Reject(sim, new SetFacilityClosedCommand(shop.Id, true)));
        sim.RunDays(1);
        Assert.Equal(0, sim.State.Stats.TotalRenters);
        Assert.Equal(0, shop.Customers);
    }

    [Fact]
    public void AClosedLift_TakesNobody_AndOpensAgain()
    {
        var sim = Demo();
        var lift = sim.State.Lifts[0];
        Assert.Null(Reject(sim, new SetLiftClosedCommand(lift.Id, true)));
        Assert.Contains("No such lift", Reject(sim, new SetLiftClosedCommand(99_999, true)));
        long riders = lift.Stats.Riders;
        sim.RunDays(1);
        Assert.Equal(riders, lift.Stats.Riders);
        Assert.Empty(lift.Queue);
        Assert.True(sim.State.Stats.TotalVisitors > 0);

        Assert.Null(Reject(sim, new SetLiftClosedCommand(lift.Id, false)));
        sim.RunDays(1);
        Assert.True(lift.Stats.Riders > riders);
    }

    [Fact]
    public void ClosingALiftMidDay_CarriesThoseAboardUp_AndEmptiesTheQueue()
    {
        var sim = Demo();
        var lift = sim.State.Lifts[0];
        sim.RunTicks(11 * 60);
        Assert.Contains(sim.State.Guests, g => g.Activity == RiderActivity.OnLift);
        Assert.Null(Reject(sim, new SetLiftClosedCommand(lift.Id, true)));
        sim.RunTicks(30);
        Assert.Empty(lift.Queue);
        Assert.DoesNotContain(sim.State.Guests, g => g.Activity is RiderActivity.OnLift or RiderActivity.Queuing);
    }
}
