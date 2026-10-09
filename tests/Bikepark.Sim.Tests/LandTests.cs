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
        Assert.False(LandMath.IsOwned(sim.State, 90_000, 80_000)); // foot forest
        Assert.Equal("foot_forest", LandMath.ParcelAt(sim.State, 90_000, 80_000)?.Id);
        Assert.Empty(sim.Events.Pending.OfType<CommandRejected>()); // the scenario's own builds are fine anywhere
    }

    // ---------------------------------------------------------------- building only on your land

    [Fact]
    public void Ways_MustStayOnYourLand_InThePreviewAndTheCommand()
    {
        var sim = Career();
        List<PointCm> intoForest = [M(740, 700), M(900, 760), M(760, 890)];
        var plan = WayPlanner.Plan(sim.Terrain, sim.Network, sim.State.TrailRules, WayKind.Trail, intoForest, LandMath.OwnedPredicate(sim.State));
        var issue = Assert.Single(plan.Issues, i => i.Code == "notYourLand");
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Equal(issue.Message, Reject(sim, new BuildWayCommand(WayKind.Trail, "Forest Run", intoForest)));
    }

    [Fact]
    public void LiftStations_Parking_AndFelling_NeedYourLand()
    {
        var sim = Career();
        var lift = StructurePlanner.PlanLift(sim.Terrain, sim.Network, sim.State, "tbar", M(300, 900), M(300, 800));
        Assert.Contains(lift.Issues, i => i.Code == "notYourLand");

        var parking = StructurePlanner.PlanParking(sim.Terrain, sim.Network, sim.State, M(250, 870), M(205, 850), 30);
        Assert.Contains(parking.Issues, i => i.Code == "notYourLand");

        var felling = ClearingPlanner.Plan(sim.Terrain, sim.Network, sim.State, M(900, 800), 2_000);
        Assert.Equal("notYourLand", felling.Issues.Single().Code);
    }

    // ---------------------------------------------------------------- buying land

    [Fact]
    public void BuyParcel_NeedsTheLevel_AndTheMoney()
    {
        var sim = Career();
        Assert.Contains("needs park level 4", Reject(sim, new BuyParcelCommand("valley_meadows")));
        Assert.Contains("Unknown parcel", Reject(sim, new BuyParcelCommand("moon")));

        var poor = Career(s =>
        {
            s.Parcels.Single(p => p.Id == "foot_forest").RequiredLevel = 0;
            s.StartingMoneyCents = 1_000;
        });
        Assert.Contains("Not enough money", Reject(poor, new BuyParcelCommand("foot_forest")));
    }

    [Fact]
    public void BuyParcel_ChargesThePrice_AndOpensTheLandForBuilding()
    {
        var sim = Career(s => s.Parcels.Single(p => p.Id == "foot_forest").RequiredLevel = 0);
        long money = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new BuyParcelCommand("foot_forest")));
        Assert.Contains(sim.Events.Pending, e => e is ParcelBought { ParcelId: "foot_forest" });
        Assert.Equal(money - 4_000_000, sim.State.Finance.MoneyCents);
        Assert.Equal(4_000_000, sim.State.Finance.TotalLandCents);
        Assert.True(LandMath.IsOwned(sim.State, 90_000, 80_000));
        Assert.Contains("already yours", Reject(sim, new BuyParcelCommand("foot_forest")));

        var felling = ClearingPlanner.Plan(sim.Terrain, sim.Network, sim.State, M(900, 800), 2_000);
        Assert.True(felling.IsValid, felling.FirstError);

        var loaded = SaveGame.Clone(sim.State);
        Assert.Equal(sim.State.OwnedParcelIds, loaded.OwnedParcelIds);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded));
    }
}
