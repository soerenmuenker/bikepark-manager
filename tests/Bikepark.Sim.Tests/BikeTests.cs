using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Safety;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>Phase 9.3: the rental fleet, bikes broken in crashes (service shop, swaps), protectors from the pro shop.</summary>
public class BikeTests
{
    private static readonly PointCm RentalSpot = new(21000, 81500), ServiceSpot = new(17000, 86500), ProShopSpot = new(13000, 85000);

    /// <summary>The Demo with its trails, rich, no reputation, after tick 0.</summary>
    private static Simulation Demo(Action<ScenarioDefinition>? tweak = null, bool collisions = false)
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.StartingMoneyCents = 100_000_000;
        tweak?.Invoke(scenario);
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        sim.State.ReputationRules.Enabled = false;
        if (!collisions) sim.State.CrashRules.CollisionDistanceCm = 0;
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

    private static Facility Build(Simulation sim, string typeId, PointCm at, int level = 0, int bikes = 0)
    {
        Assert.Null(Reject(sim, new BuildFacilityCommand(typeId, at, new PointCm(at.X + 1000, at.Z), Level: level, Bikes: bikes)));
        return sim.State.Facilities[^1];
    }

    // ---------------------------------------------------------------- the fleet

    [Fact]
    public void Bikes_AreBoughtForTheRack_AtTheStationsPrice()
    {
        var sim = Demo();
        var station = Build(sim, "rental_station", RentalSpot);
        var type = FacilityMath.FindType(sim.State, "rental_station")!;
        long money = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new BuyRentalBikesCommand(station.Id, 5)));
        Assert.Equal(5, station.Fleet.Count);
        Assert.All(station.Fleet, b => Assert.Equal(1000, b.ConditionPermille));
        Assert.Equal(money - 5 * type.BikeCostCents, sim.State.Finance.MoneyCents);
        Assert.Contains("rack holds", Reject(sim, new BuyRentalBikesCommand(station.Id, type.MaxFleet)));
        Assert.Contains("1 to 50", Reject(sim, new BuyRentalBikesCommand(station.Id, 0)));
        var service = Build(sim, "service_shop", ServiceSpot);
        Assert.Contains("no rental station", Reject(sim, new BuyRentalBikesCommand(service.Id, 1)));

        var poor = Demo(s => s.StartingMoneyCents = 1_100_000);
        var cheap = Build(poor, "rental_station", RentalSpot);
        Assert.Contains("Not enough money", Reject(poor, new BuyRentalBikesCommand(cheap.Id, 5)));
        Assert.Contains("can't take", Reject(poor, new BuildFacilityCommand("service_shop", ServiceSpot, new PointCm(18000, 86500), Bikes: 3)));
    }

    [Fact]
    public void Renters_ComeOnlyForAFreeBike_PayForIt_AndAreMostlyBeginners()
    {
        var empty = Demo();
        Build(empty, "rental_station", RentalSpot);
        empty.RunTicks(12 * 60);
        Assert.Equal(0, empty.State.Stats.TotalRenters);
        Assert.True(empty.State.Stats.RentersWithoutBike > 0);

        var sim = Demo();
        var station = Build(sim, "rental_station", RentalSpot, bikes: 20);
        sim.RunTicks(12 * 60);
        var renters = sim.State.Guests.Where(g => g.RentalBikeId != 0).ToList();
        Assert.NotEmpty(renters);
        Assert.Equal(renters.Count, renters.Select(g => g.RentalBikeId).Distinct().Count());
        Assert.All(renters, g => Assert.Equal(g.Id, station.Fleet.Single(b => b.Id == g.RentalBikeId).RentedBy));
        Assert.All(renters, g => Assert.True(g.Skill <= sim.State.FacilityRules.RenterSkillMax));
        Assert.All(renters, g => Assert.False(g.EBike));
        var rental = FacilityMath.FindProduct(sim.State, "rental")!;
        Assert.Equal(sim.State.Stats.TotalRenters * rental.ReferencePriceCents, sim.State.Finance.TotalRentalCents);

        // At night every bike is back in the rack, a little worn.
        sim.RunTicks(GameTime.MinutesPerDay - (int)(sim.State.Tick % GameTime.MinutesPerDay));
        Assert.All(station.Fleet, b => Assert.Equal(0, b.RentedBy));
        Assert.Contains(station.Fleet, b => b.ConditionPermille < 1000);
    }

    [Fact]
    public void DearRentals_FewerRenters()
    {
        static long Renters(long? price)
        {
            var sim = Demo();
            Build(sim, "rental_station", RentalSpot, bikes: 20);
            if (price is { } p) Assert.Null(Reject(sim, new SetProductPriceCommand("rental", p)));
            sim.RunDays(1);
            return sim.State.Stats.TotalRenters;
        }
        long fair = Renters(null), dear = Renters(6_500);
        Assert.True(fair > 10, $"{fair} renters");
        Assert.True(dear * 3 < fair, $"{dear} vs {fair} renters");
    }

    [Fact]
    public void WornOutBikes_AreRetired()
    {
        var sim = Demo();
        var station = Build(sim, "rental_station", RentalSpot, bikes: 10);
        foreach (var bike in station.Fleet) bike.ConditionPermille = 1;
        var retired = new List<RentalBikeRetired>();
        sim.Events.Subscribe<RentalBikeRetired>(retired.Add);
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.NotEmpty(retired);
        Assert.Equal(retired.Count, sim.State.Stats.BikesRetired);
        Assert.Equal(10 - retired.Count, station.Fleet.Count);
    }

    [Fact]
    public void EBikes_BringAnyone_AndClimbFaster()
    {
        var sim = Demo();
        Build(sim, "rental_station", RentalSpot, level: 2, bikes: 20);
        sim.RunTicks(12 * 60);
        var renters = sim.State.Guests.Where(g => g.RentalBikeId != 0).ToList();
        Assert.NotEmpty(renters);
        Assert.All(renters, g => Assert.True(g.EBike));

        var rules = new TrailRules();
        var climb = new WaySegment(0, 0, 1000, 120, 0, 0, 0, 0, default, 300);
        var plain = new Guest { Skill = 400 };
        var motor = new Guest { Skill = 400, EBike = true };
        Assert.True(RiderSystem.Speed(motor, rules, WayKind.AccessPath, climb, 120) > RiderSystem.Speed(plain, rules, WayKind.AccessPath, climb, 120));
    }

    // ---------------------------------------------------------------- broken bikes

    [Fact]
    public void BrokenBikes_AreFixedAtTheServiceShop_AndTheRiderRidesOn()
    {
        var sim = Demo(s => s.CrashRules.BikeOnlyPermille = 1000, collisions: true);
        var shop = Build(sim, "service_shop", ServiceSpot);
        var fixedBikes = new List<BikeFixed>();
        sim.Events.Subscribe<BikeFixed>(fixedBikes.Add);
        var crashes = new List<RiderCrashed>();
        sim.Events.Subscribe<RiderCrashed>(crashes.Add);
        int most = 0;
        for (int t = 0; t < GameTime.MinutesPerDay; t++)
        {
            sim.Step();
            most = Math.Max(most, FacilityMath.SeatsTaken(sim.State, shop));
        }
        sim.Events.Dispatch();
        Assert.Contains(crashes, c => c.BikeOnly && c.Severity == InjurySeverity.None);
        Assert.DoesNotContain(crashes, c => c.Severity == InjurySeverity.Minor); // every minor crash only broke the bike
        Assert.NotEmpty(fixedBikes);
        Assert.All(fixedBikes, f => Assert.False(f.Swapped));
        Assert.Equal(fixedBikes.Count, sim.State.Stats.TotalRepairs);
        Assert.True(most <= FacilityMath.Seats(FacilityMath.FindType(sim.State, shop.TypeId)!, shop));
        Assert.Equal(sim.State.Stats.TotalRepairs * FacilityMath.FindProduct(sim.State, "repair")!.ReferencePriceCents, sim.State.Finance.TotalRepairCents);
    }

    [Fact]
    public void WithoutAServiceShop_BrokenBikesSendRidersHome()
    {
        var sim = Demo(s => s.CrashRules.BikeOnlyPermille = 1000, collisions: true);
        var left = new List<GuestLeft>();
        sim.Events.Subscribe<GuestLeft>(left.Add);
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.Contains(left, l => l.Reason == GuestLeaveReason.BikeBroken);
        Assert.Equal(0, sim.State.Stats.TotalRepairs);
    }

    [Fact]
    public void Renters_SwapABrokenBike_AtTheStation()
    {
        var sim = Demo(s =>
        {
            s.CrashRules.BikeOnlyPermille = 1000;
            s.FacilityRules = new FacilityRules { RenterSharePermille = 3000 };
        }, collisions: true);
        Build(sim, "rental_station", RentalSpot, level: 1, bikes: 40);
        var fixedBikes = new List<BikeFixed>();
        sim.Events.Subscribe<BikeFixed>(fixedBikes.Add);
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.Contains(fixedBikes, f => f.Swapped);
    }

    [Fact]
    public void Protectors_HalveTheSeriousShare_AndSellAtTheProShop()
    {
        var rules = new CrashRules();
        int plain = CrashMath.SeriousPermille(rules, CrashCause.Feature, jump: true, 600, 500);
        Assert.Equal(plain * rules.ProtectedSeriousPermille / 1000, CrashMath.SeriousPermille(rules, CrashCause.Feature, jump: true, 600, 500, protectedRider: true));

        var sim = Demo();
        Build(sim, "pro_shop", ProShopSpot);
        sim.RunDays(1);
        var sold = sim.State.Finance.ProductSales.SingleOrDefault(p => p.ProductId == "protectors");
        Assert.NotNull(sold);
        Assert.True(sold.Sold > 0);
    }

    [Fact]
    public void TearingDownTheRentalStation_SendsItsRentersHome()
    {
        var sim = Demo();
        var station = Build(sim, "rental_station", RentalSpot, bikes: 20);
        sim.RunTicks(11 * 60);
        var renters = sim.State.Guests.Where(g => g.RentalBikeId != 0).Select(g => g.Id).ToList();
        Assert.NotEmpty(renters);
        Assert.Null(Reject(sim, new RenaturalizeStructureCommand(station.Id)));
        Assert.All(sim.State.Guests.Where(g => renters.Contains(g.Id)), g => Assert.Equal(0, g.RentalBikeId));
        sim.RunTicks(3 * 60);
        Assert.DoesNotContain(sim.State.Guests, g => renters.Contains(g.Id) && g.Activity == RiderActivity.Idle);
    }

    [Fact]
    public void ShippedBikeBuildings_Validate()
    {
        var types = ScenarioLoader.LoadFacilityCatalog(Path.Combine(TestWorlds.RepoRoot(), "data", "facility_types.json"));
        Assert.Contains(types, t => t.Kind == FacilityKind.Rental && t.MaxFleet > 0 && t.Upgrades.Any(u => u.EBikes));
        Assert.Contains(types, t => t.Kind == FacilityKind.Service && t.Seats > 0);
        Assert.Contains(types, t => t.Kind == FacilityKind.Shop && t.Products.Contains("protectors"));
        Assert.True(Ledger.IsIncome(MoneyCategory.Rentals) && Ledger.IsIncome(MoneyCategory.Repairs) && !Ledger.IsIncome(MoneyCategory.BikeCosts));
    }
}
