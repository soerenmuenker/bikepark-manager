using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>Bars and shops near gravel paths and hubs: lunch, snacks, drinks and merch at the park's prices; upgrades.</summary>
public class FacilityTests
{
    private static readonly PointCm BarSpot = new(17500, 83000), ShopSpot = new(19500, 89500); // demo_facilities.json

    private static PointCm East(PointCm p) => new(p.X + 1000, p.Z);

    /// <summary>The Demo with its trails, without buildings, after tick 0.</summary>
    private static Simulation Demo(Action<ScenarioDefinition>? tweak = null)
    {
        var scenario = TestWorlds.LiftScenario();
        tweak?.Invoke(scenario);
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
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
    public void ShippedCatalog_HasASnackBarFirst_AndValidates()
    {
        var types = ScenarioLoader.LoadFacilityCatalog(Path.Combine(TestWorlds.RepoRoot(), "data", "facility_types.json"));
        Assert.All(types, t => Assert.Empty(t.Validate()));
        Assert.Equal(0, types.Where(t => t.Kind == FacilityKind.Bar).Min(t => t.RequiredLevel));
        Assert.Contains(types, t => t.Kind == FacilityKind.Shop);
    }

    [Fact]
    public void Building_NeedsGravelAccess_AndCostsItsPrice()
    {
        var sim = Demo();
        var type = FacilityMath.FindType(sim.State, "snack_bar")!;
        Assert.Contains("gravel path", Reject(sim, new BuildFacilityCommand("snack_bar", new PointCm(50_000, 50_000), new PointCm(51_000, 50_000))));
        Assert.Contains("Unknown building", Reject(sim, new BuildFacilityCommand("casino", BarSpot, East(BarSpot))));

        long money = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new BuildFacilityCommand("snack_bar", BarSpot, East(BarSpot))));
        var bar = Assert.Single(sim.State.Facilities);
        Assert.Equal(0, bar.Level);
        Assert.Equal(money - type.BuildCostCents, sim.State.Finance.MoneyCents);
        Assert.Equal(type.BuildCostCents, sim.State.Finance.TotalFacilityBuildCents);
        Assert.Contains("overlaps another structure", Reject(sim, new BuildFacilityCommand("merch_shop", BarSpot, East(BarSpot))));

        var poor = Demo(s => s.StartingMoneyCents = 100);
        Assert.Contains("Not enough money", Reject(poor, new BuildFacilityCommand("snack_bar", BarSpot, East(BarSpot))));
        var gated = Demo(s => s.Rules.UnlockAll = false);
        Assert.Contains("needs park level", Reject(gated, new BuildFacilityCommand("bike_cafe", BarSpot, East(BarSpot))));
    }

    [Fact]
    public void Lunch_IsAtTheBar_UpToItsSeats_OthersRideOn_ThenEatPacked()
    {
        var sim = Demo(s => s.StartingMoneyCents = 100_000_000);
        sim.State.ReputationRules.Enabled = false;
        sim.State.CrashRules.CollisionDistanceCm = 0;
        Assert.Null(Reject(sim, new BuildFacilityCommand("snack_bar", BarSpot, East(BarSpot))));
        var bar = sim.State.Facilities[0];
        var type = FacilityMath.FindType(sim.State, bar.TypeId)!;
        type.Seats = 5;
        var lunches = new List<GuestAteLunch>();
        sim.Events.Subscribe<GuestAteLunch>(lunches.Add);
        int most = 0;
        for (int t = 0; t < GameTime.MinutesPerDay; t++)
        {
            sim.Step();
            most = Math.Max(most, FacilityMath.SeatsTaken(sim.State, bar));
        }
        sim.Events.Dispatch();

        var atBar = lunches.Where(l => l.FacilityId == bar.Id).ToList();
        var packed = lunches.Where(l => l.FacilityId == 0).ToList();
        Assert.True(atBar.Count > 10, $"{atBar.Count} lunches at the bar");
        Assert.Equal(5, most); // never more than the seats
        long lunchPrice = FacilityMath.PriceOf(sim.State, FacilityMath.FindProduct(sim.State, "lunch")!);
        Assert.All(atBar, l => Assert.Equal(lunchPrice, l.PaidCents));
        Assert.All(packed, l => Assert.Equal(0, l.PaidCents));
        Assert.All(packed, l => Assert.True(GameTime.MinuteOfDay(l.Tick) >= sim.State.Rules.LunchEndMinute || l.PaidCents == 0));
        Assert.Equal(atBar.Count, sim.State.Finance.ProductSales.Single(p => p.ProductId == "lunch").Sold);
        Assert.True(bar.TotalRevenueCents >= atBar.Sum(l => l.PaidCents)); // plus snacks
        Assert.Equal(atBar.Sum(l => l.PaidCents), sim.State.Finance.TotalFoodCents);
    }

    [Fact]
    public void WithoutABar_GuestsEatPacked_AndBuyNoSnacks()
    {
        var sim = Demo();
        sim.State.CrashRules.CollisionDistanceCm = 0;
        var lunches = new List<GuestAteLunch>();
        sim.Events.Subscribe<GuestAteLunch>(lunches.Add);
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.NotEmpty(lunches);
        Assert.All(lunches, l => Assert.Equal(0, l.PaidCents));
        Assert.Equal(0, sim.State.Finance.TotalFoodCents);
        Assert.Equal(0, sim.State.Finance.TotalSnackCents);
    }

    [Fact]
    public void LeavingGuests_BuyMerch_TheHappierTheLikelier()
    {
        var sim = Demo(s => s.StartingMoneyCents = 100_000_000);
        sim.State.CrashRules.CollisionDistanceCm = 0;
        Assert.Null(Reject(sim, new BuildFacilityCommand("merch_shop", ShopSpot, East(ShopSpot))));
        var shop = sim.State.Facilities[0];
        var type = FacilityMath.FindType(sim.State, shop.TypeId)!;
        var sold = new List<MerchSold>();
        sim.Events.Subscribe<MerchSold>(sold.Add);
        sim.RunDays(1);
        sim.Events.Dispatch();

        Assert.True(sold.Count > 3, $"{sold.Count} sold");
        Assert.Equal(sold.Count, shop.Customers);
        var merch = FacilityMath.FindProduct(sim.State, "merch")!;
        Assert.Equal(sold.Count * merch.ReferencePriceCents, sim.State.Finance.TotalMerchCents);
        Assert.Equal(sold.Count * (merch.ReferencePriceCents * merch.GoodsCostPermille / 1000), sim.State.Finance.TotalMerchCostCents);
    }

    [Fact]
    public void Buildings_CostUpkeep_AndCanBeTornDown()
    {
        var sim = Demo(s => s.StartingMoneyCents = 100_000_000);
        Assert.Null(Reject(sim, new BuildFacilityCommand("snack_bar", BarSpot, East(BarSpot))));
        var type = FacilityMath.FindType(sim.State, "snack_bar")!;
        var days = new List<DayReport>();
        sim.Events.Subscribe<DayEnded>(e => days.Add(e.Report));
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.Equal(type.UpkeepPerDayCents, days.Single().FacilityUpkeepCents);

        int id = sim.State.Facilities[0].Id;
        long money = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new RenaturalizeStructureCommand(id)));
        Assert.Empty(sim.State.Facilities);
        Assert.DoesNotContain(sim.State.TerrainEdits, e => e.OwnerId == id);
        Assert.Equal(money - type.BuildCostCents * sim.State.LiftRules.RemovalPermille / 1000, sim.State.Finance.MoneyCents);
    }

    [Fact]
    public void Facilities_SurviveASave_AndRunDeterministically()
    {
        var sim = Demo(s => s.StartingMoneyCents = 100_000_000);
        foreach (var c in TestWorlds.DemoFacilities()) sim.Commands.Enqueue(c.Command);
        sim.RunTicks(12 * 60);
        Assert.Equal(TestWorlds.DemoFacilities().Count, sim.State.Facilities.Count);
        var loaded = new Simulation(SaveGame.Clone(sim.State));
        sim.RunTicks(300);
        loaded.RunTicks(300);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded.State));
    }
}
