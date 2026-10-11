using Bikepark.Sim.Commands;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Land;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class LandTests
{
    private static PointCm M(int x, int z) => new(x * 100, z * 100); // metres

    // Starter Valley parcels: the ski hill (owned), the South slope west of it (level 3) and Hikers Paradise with both
    // gondola stations (level 8).
    private const string SouthSlope = "parcel_5", HikersParadise = "parcel_6";

    private static Simulation Career(Action<ScenarioDefinition>? tweak = null)
    {
        var scenario = TestWorlds.CareerScenario();
        tweak?.Invoke(scenario);
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        sim.Step(); // scenario commands
        return sim;
    }

    private static string? Reject(Simulation sim, ICommand command)
    {
        sim.Events.Clear();
        sim.Commands.Enqueue(command);
        sim.Step();
        return sim.Events.Pending.OfType<CommandRejected>().SingleOrDefault()?.Reason;
    }

    // ---------------------------------------------------------------- geometry

    [Fact]
    public void Contains_HandlesConvexAndConcaveOutlines()
    {
        List<PointCm> square = [new(0, 0), new(1000, 0), new(1000, 1000), new(0, 1000)];
        Assert.True(LandMath.Contains(square, 500, 500));
        Assert.True(LandMath.Contains(square, 0, 500));
        Assert.False(LandMath.Contains(square, 1500, 500));
        Assert.False(LandMath.Contains(square, 500, -1));

        // An L: the notch (top right) is outside.
        List<PointCm> l = [new(0, 0), new(500, 0), new(500, 500), new(1000, 500), new(1000, 1000), new(0, 1000)];
        Assert.True(LandMath.Contains(l, 250, 250));
        Assert.True(LandMath.Contains(l, 750, 750));
        Assert.False(LandMath.Contains(l, 750, 250));
        Assert.Equal(100, LandMath.AreaSquareMeters(square));
    }

    [Fact]
    public void WithoutParcels_AllLandIsThePark()
    {
        var sim = TestWorlds.RunLift(1337, 0);
        Assert.Empty(sim.State.Parcels);
        Assert.True(LandMath.IsOwned(sim.State, 100, 100));
        Assert.Null(LandMath.OwnedPredicate(sim.State));
    }

    [Fact]
    public void Career_StartsWithTheSkiHillOnly()
    {
        var sim = Career();
        Assert.Equal(["ski_hill"], sim.State.OwnedParcelIds);
        Assert.True(LandMath.IsOwned(sim.State, 74_000, 80_000));
        Assert.False(LandMath.IsOwned(sim.State, 20_500, 85_000)); // gondola valley station
        Assert.False(LandMath.IsOwned(sim.State, 60_000, 90_000)); // south slope
        Assert.Equal(SouthSlope, LandMath.ParcelAt(sim.State, 60_000, 90_000)?.Id);
        Assert.Empty(sim.Events.Pending.OfType<CommandRejected>()); // the scenario's own builds are fine anywhere
    }

    // ---------------------------------------------------------------- building only on your land

    [Fact]
    public void Ways_MustStayOnYourLand_InThePreviewAndTheCommand()
    {
        var sim = Career();
        List<PointCm> intoSouthSlope = [M(740, 760), M(700, 830), M(640, 900)];
        var plan = WayPlanner.Plan(sim.Terrain, sim.Network, sim.State.TrailRules, WayKind.Trail, intoSouthSlope, LandMath.OwnedPredicate(sim.State));
        var issue = Assert.Single(plan.Issues, i => i.Code == "notYourLand");
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal(issue.Message, Reject(sim, new BuildWayCommand(WayKind.Trail, "South Run", intoSouthSlope)));
    }

    [Fact]
    public void LiftStations_Parking_AndFelling_NeedYourLand()
    {
        var sim = Career();
        var lift = StructurePlanner.PlanLift(sim.Terrain, sim.Network, sim.State, "tbar", M(300, 900), M(300, 800));
        Assert.Contains(lift.Issues, i => i.Code == "notYourLand");

        var parking = StructurePlanner.PlanParking(sim.Terrain, sim.Network, sim.State, M(250, 870), M(205, 850), 30);
        Assert.Contains(parking.Issues, i => i.Code == "notYourLand");

        var felling = ClearingPlanner.Plan(sim.Terrain, sim.Network, sim.State, FellingSpot, 2_000);
        Assert.Equal("notYourLand", felling.Issues.Single().Code);
    }

    /// <summary>Woods on the South slope, just west of the ski hill.</summary>
    private static readonly PointCm FellingSpot = M(600, 900);

    // ---------------------------------------------------------------- buying land

    [Fact]
    public void BuyParcel_NeedsTheLevel_AndTheMoney()
    {
        var sim = Career();
        Assert.Contains("needs park level 8", Reject(sim, new BuyParcelCommand(HikersParadise)));
        Assert.Contains("Unknown parcel", Reject(sim, new BuyParcelCommand("moon")));

        var poor = Career(s => s.Parcels.Single(p => p.Id == SouthSlope).RequiredLevel = 0); // 200 000 € > the 150 000 € start
        Assert.Contains("Not enough money", Reject(poor, new BuyParcelCommand(SouthSlope)));
    }

    [Fact]
    public void BuyParcel_ChargesThePrice_AndOpensTheLandForBuilding()
    {
        var sim = Career(s =>
        {
            s.Parcels.Single(p => p.Id == SouthSlope).RequiredLevel = 0;
            s.StartingMoneyCents = 30_000_000;
        });
        long money = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new BuyParcelCommand(SouthSlope)));
        Assert.Contains(sim.Events.Pending, e => e is ParcelBought { ParcelId: SouthSlope });
        Assert.Equal(money - 20_000_000, sim.State.Finance.MoneyCents);
        Assert.Equal(20_000_000, sim.State.Finance.TotalLandCents);
        Assert.True(LandMath.IsOwned(sim.State, 60_000, 90_000));
        Assert.Contains("already yours", Reject(sim, new BuyParcelCommand(SouthSlope)));

        var felling = ClearingPlanner.Plan(sim.Terrain, sim.Network, sim.State, FellingSpot, 2_000);
        Assert.True(felling.IsValid, felling.FirstError);

        var loaded = SaveGame.Clone(sim.State);
        Assert.Equal(sim.State.OwnedParcelIds, loaded.OwnedParcelIds);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded));
    }
}
