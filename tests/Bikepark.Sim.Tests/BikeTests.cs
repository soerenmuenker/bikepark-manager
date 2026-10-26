using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.Safety;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>
/// Bike shops: departments, range, the workshop (broken bikes, tune-ups, suspension setups, parts), the rental fleet
/// (rental counter, e-bikes, swaps, fleet care) and protectors.
/// </summary>
public class BikeTests
{
    private static readonly PointCm ShopSpot = new(25000, 90000); // demo_facilities.json

    // The upgrades in data/facility_types.json: second workbench, rental counter, bigger store, bigger rack, suspension corner,
    // e-bike fleet, demo center.
    private const int WithWorkshop = 1, WithRental = 2, WithBigRack = 4, WithSuspension = 5, WithEBikes = 6, WithDemoCenter = 7;

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

    private static Facility Build(Simulation sim, PointCm at, int level = 0, int bikes = 0, List<string>? departments = null)
    {
        Assert.Null(Reject(sim, new BuildFacilityCommand("bike_shop", at, new PointCm(at.X + 1000, at.Z), Level: level, Bikes: bikes,
            Departments: departments)));
        return sim.State.Facilities[^1];
    }

    private static Facility Shop(Simulation sim, int level = 0, int bikes = 0, List<string>? departments = null) =>
        Build(sim, ShopSpot, level, bikes, departments);

    private static long Sold(Simulation sim, string productId) =>
        sim.State.Finance.ProductSales.SingleOrDefault(p => p.ProductId == productId)?.Sold ?? 0;

    // ---------------------------------------------------------------- the fleet

    [Fact]
    public void Bikes_AreBoughtForTheRack_OnceTheRentalCounterIsBuilt()
    {
        var sim = Demo();
        var bare = Shop(sim);
        Assert.Contains("no rental counter", Reject(sim, new BuyRentalBikesCommand(bare.Id, 1)));
        Assert.Null(Reject(sim, new RenaturalizeStructureCommand(bare.Id)));

        var shop = Shop(sim, WithRental);
        var type = FacilityMath.FindType(sim.State, "bike_shop")!;
        long money = sim.State.Finance.MoneyCents;
        var bought = new List<RentalBikesBought>();
        sim.Events.Subscribe<RentalBikesBought>(bought.Add);
        Assert.Null(Reject(sim, new BuyRentalBikesCommand(shop.Id, 5)));
        sim.Events.Dispatch();
        Assert.Equal(5, Assert.Single(bought).Count);
        Assert.Equal(5, shop.Fleet.Count);
        Assert.All(shop.Fleet, b => Assert.Equal(1000, b.ConditionPermille));
        Assert.Equal(money - 5 * type.BikeCostCents, sim.State.Finance.MoneyCents);
        Assert.Contains("rack holds", Reject(sim, new BuyRentalBikesCommand(shop.Id, FacilityMath.MaxFleet(type, shop))));
        Assert.Contains("1 to 50", Reject(sim, new BuyRentalBikesCommand(shop.Id, 0)));

        long building = type.BuildCostCents + type.Upgrades.Take(WithRental).Sum(u => u.CostCents);
        var poor = Demo(s => s.StartingMoneyCents = building + 100_000);
        var cheap = Shop(poor, WithRental);
        Assert.Contains("Not enough money", Reject(poor, new BuyRentalBikesCommand(cheap.Id, 5)));
        Assert.Contains("can't take", Reject(poor, new BuildFacilityCommand("bike_shop", ShopSpot, new PointCm(26000, 90000), Bikes: 3)));
        Assert.Contains("bike shops", Reject(poor, new BuildFacilityCommand("snack_bar", ShopSpot, new PointCm(26000, 90000), Range: ShopRange.Premium)));
    }

    [Fact]
    public void Renters_ComeOnlyForAFreeBike_PayForIt_AndAreMostlyBeginners()
    {
        var empty = Demo();
        Shop(empty, WithRental);
        empty.RunTicks(12 * 60);
        Assert.Equal(0, empty.State.Stats.TotalRenters);
        Assert.True(empty.State.Stats.RentersWithoutBike > 0);

        var sim = Demo();
        var shop = Shop(sim, WithRental, bikes: 20);
        sim.RunTicks(12 * 60);
        var renters = sim.State.Guests.Where(g => g.RentalBikeId != 0).ToList();
        Assert.NotEmpty(renters);
        Assert.Equal(renters.Count, renters.Select(g => g.RentalBikeId).Distinct().Count());
        Assert.All(renters, g => Assert.Equal(g.Id, shop.Fleet.Single(b => b.Id == g.RentalBikeId).RentedBy));
        Assert.All(renters, g => Assert.True(g.Skill <= sim.State.FacilityRules.RenterSkillMax));
        Assert.All(renters, g => Assert.False(g.EBike));
        var rental = FacilityMath.FindProduct(sim.State, "rental")!;
        Assert.Equal(sim.State.Stats.TotalRenters * rental.ReferencePriceCents, sim.State.Finance.TotalRentalCents);

        // At night every bike is back in the rack.
        sim.RunTicks(GameTime.MinutesPerDay - 1 - (int)(sim.State.Tick % GameTime.MinutesPerDay));
        Assert.All(shop.Fleet, b => Assert.Equal(0, b.RentedBy));
        Assert.Contains(shop.Fleet, b => b.ConditionPermille < 1000);
    }

    [Fact]
    public void DearRentals_FewerRenters()
    {
        static long Renters(long? price)
        {
            var sim = Demo();
            Shop(sim, WithRental, bikes: 20);
            if (price is { } p) Assert.Null(Reject(sim, new SetProductPriceCommand("rental", p)));
            sim.RunDays(1);
            return sim.State.Stats.TotalRenters;
        }
        long fair = Renters(null), dear = Renters(6_500);
        Assert.True(fair > 10, $"{fair} renters");
        Assert.True(dear * 2 < fair, $"{dear} vs {fair} renters");
    }

    [Fact]
    public void WornOutBikes_AreRetired()
    {
        var sim = Demo();
        var shop = Shop(sim, WithRental, bikes: 10);
        foreach (var bike in shop.Fleet) bike.ConditionPermille = 1;
        var retired = new List<RentalBikeRetired>();
        sim.Events.Subscribe<RentalBikeRetired>(retired.Add);
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.NotEmpty(retired);
        Assert.Equal(retired.Count, sim.State.Stats.BikesRetired);
        Assert.Equal(10 - retired.Count, shop.Fleet.Count);
    }

    [Fact]
    public void FleetCare_RestoresTheWorstBikesFirst_ForParts_AndCanBeSwitchedOff()
    {
        var sim = Demo();
        var shop = Shop(sim, WithRental, bikes: 4);
        var type = FacilityMath.FindType(sim.State, shop.TypeId)!;
        var rules = sim.State.FacilityRules;
        int[] before = [100, 900, 300, 1000];
        for (int i = 0; i < 4; i++) shop.Fleet[i].ConditionPermille = before[i];
        long budget = FacilityMath.Workbenches(type, shop) * rules.FleetCarePerBenchPermille; // 2 benches: 800
        long money = sim.State.Finance.MoneyCents;
        Bikes.FleetCare(sim.State);
        Assert.Equal(800, budget);
        Assert.Equal(900, shop.Fleet[0].ConditionPermille); // worst first: all 800 go to the bike at 10 %
        Assert.Equal(300, shop.Fleet[2].ConditionPermille);
        Assert.Equal(budget, shop.FleetCareLastNight);
        Assert.Equal(money - budget * rules.FleetCareCentsPerPermille, sim.State.Finance.MoneyCents);

        Assert.Null(Reject(sim, new SetWorkshopServiceCommand(shop.Id, FacilityMath.FleetCare, false)));
        int[] now = shop.Fleet.Select(b => b.ConditionPermille).ToArray();
        Bikes.FleetCare(sim.State);
        Assert.Equal(now, shop.Fleet.Select(b => b.ConditionPermille).ToArray());
        Assert.Equal(0, shop.FleetCareLastNight);
    }

    [Fact]
    public void EBikes_BringAnyone_AndClimbFaster()
    {
        var sim = Demo();
        Shop(sim, WithEBikes, bikes: 20);
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

    // ---------------------------------------------------------------- the workshop

    [Fact]
    public void BrokenBikes_AreFixedInTheWorkshop_AndTheRiderRidesOn()
    {
        var sim = Demo(s => s.CrashRules.BikeOnlyPermille = 1000, collisions: true);
        var shop = Shop(sim);
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
        Assert.True(most <= FacilityMath.Workbenches(FacilityMath.FindType(sim.State, shop.TypeId)!, shop));
        Assert.Equal(sim.State.Stats.TotalRepairs * FacilityMath.FindProduct(sim.State, "repair")!.ReferencePriceCents, sim.State.Finance.TotalRepairCents);
    }

    [Fact]
    public void Parts_SellWithRepairs_WhenOnTheShelves()
    {
        var sim = Demo(s => s.CrashRules.BikeOnlyPermille = 1000, collisions: true);
        Shop(sim, departments: ["parts"]);
        FacilityMath.FindProduct(sim.State, "parts")!.WithServicePermille = 1000;
        sim.RunDays(1);
        Assert.True(sim.State.Stats.TotalRepairs > 0);
        Assert.True(Sold(sim, "parts") > 0);
        Assert.True(Sold(sim, "parts") <= sim.State.Stats.TotalRepairs);
        Assert.Equal(0, Sold(sim, "merch")); // not on the shelves
    }

    [Fact]
    public void WithoutABikeShop_BrokenBikesSendRidersHome()
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
    public void Renters_SwapABrokenBike_AtTheShop()
    {
        var sim = Demo(s =>
        {
            s.CrashRules.BikeOnlyPermille = 1000;
            s.FacilityRules = new FacilityRules { RenterSharePermille = 3000 };
        }, collisions: true);
        Shop(sim, WithBigRack, bikes: 40);
        var fixedBikes = new List<BikeFixed>();
        sim.Events.Subscribe<BikeFixed>(fixedBikes.Add);
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.Contains(fixedBikes, f => f.Swapped);
    }

    [Fact]
    public void TuneUps_AreBookedOnce_AndKeepBikesWhole()
    {
        static (long BikeOnly, long TuneUps, Simulation Sim) Day(bool tuneUps)
        {
            var sim = Demo(s => s.CrashRules.BikeOnlyPermille = 1000, collisions: true);
            var shop = Shop(sim, WithWorkshop);
            var type = FacilityMath.FindType(sim.State, shop.TypeId)!;
            type.Workbenches = 100;
            var tuneUp = FacilityMath.FindProduct(sim.State, "tuneup")!;
            tuneUp.ServiceChancePermille = 1000;
            tuneUp.TuneUpPermille = 1;
            if (!tuneUps) Assert.Null(Reject(sim, new SetWorkshopServiceCommand(shop.Id, "tuneup", false)));
            sim.RunDays(1);
            return (sim.State.Safety.BikeOnlyCrashes, Sold(sim, "tuneup"), sim);
        }
        var with = Day(true);
        var without = Day(false);
        Assert.True(with.TuneUps > 20, $"{with.TuneUps} tune-ups");
        Assert.Equal(0, without.TuneUps);
        Assert.True(with.BikeOnly * 2 < without.BikeOnly, $"{with.BikeOnly} vs {without.BikeOnly} bike-only crashes");
        Assert.Equal(with.TuneUps * FacilityMath.FindProduct(with.Sim.State, "tuneup")!.ReferencePriceCents
                     + with.Sim.State.Stats.TotalRepairs * FacilityMath.FindProduct(with.Sim.State, "repair")!.ReferencePriceCents,
            with.Sim.State.Finance.TotalRepairCents);
        Assert.True(with.TuneUps <= with.Sim.State.Stats.TotalVisitors);
    }

    [Fact]
    public void TheReservedBench_StaysFreeOfServices()
    {
        var sim = Demo();
        var shop = Shop(sim, WithWorkshop); // two benches
        FacilityMath.FindProduct(sim.State, "tuneup")!.ServiceChancePermille = 1000;
        Assert.Null(Reject(sim, new SetReserveBenchCommand(shop.Id, true)));
        int most = 0;
        for (int t = 0; t < GameTime.MinutesPerDay; t++)
        {
            sim.Step();
            most = Math.Max(most, FacilityMath.SeatsTaken(sim.State, shop));
        }
        Assert.Equal(1, most);
        Assert.True(Sold(sim, "tuneup") > 0);
    }

    [Fact]
    public void SuspensionSetups_AddFunOnRedAndBlack_Only()
    {
        var blue = new TrailFeatureType { Id = "berm", Difficulty = 300, FlowAffinity = 500, TechAffinity = 500 };
        var red = new TrailFeatureType { Id = "kicker", Difficulty = 650, FlowAffinity = 500, TechAffinity = 500 };
        var plain = new Guest { Skill = 700, Style = RiderStyle.Flow };
        var setUp = new Guest { Skill = 700, Style = RiderStyle.Flow, RedBlackFun = 80 };
        Assert.Equal(RiderSystem.FeatureFun(plain, blue), RiderSystem.FeatureFun(setUp, blue));
        Assert.Equal(Math.Min(1000, RiderSystem.FeatureFun(plain, red) + 80), RiderSystem.FeatureFun(setUp, red));

        var sim = Demo();
        Shop(sim, WithSuspension);
        sim.RunDays(1);
        Assert.True(Sold(sim, "suspension") > 0);
    }

    // ---------------------------------------------------------------- departments and range

    [Fact]
    public void Departments_FillTheShelves_UpToTheSlots_OnlyUnlockedOnes()
    {
        var sim = Demo();
        var shop = Shop(sim);
        var type = FacilityMath.FindType(sim.State, shop.TypeId)!;
        Assert.Equal(["merch", "clothing", "protectors"], shop.Departments); // the first ones, catalog order
        Assert.Contains("parts", FacilityMath.UnlockedDepartments(sim.State, type, shop).Select(p => p.Id));
        Assert.DoesNotContain("parts", FacilityMath.ProductsOf(sim.State, type, shop).Select(p => p.Id));

        Assert.Contains("shelf slots", Reject(sim, new SetShopDepartmentsCommand(shop.Id, ["merch", "clothing", "protectors", "parts"])));
        Assert.Contains("can't stock", Reject(sim, new SetShopDepartmentsCommand(shop.Id, ["bikes"])));
        Assert.Contains("only once", Reject(sim, new SetShopDepartmentsCommand(shop.Id, ["merch", "merch"])));
        Assert.Null(Reject(sim, new SetShopDepartmentsCommand(shop.Id, ["parts", "merch"])));
        Assert.Equal(["merch", "parts"], shop.Departments);
        Assert.Contains("parts", FacilityMath.ProductsOf(sim.State, type, shop).Select(p => p.Id));
        Assert.DoesNotContain("clothing", FacilityMath.ProductsOf(sim.State, type, shop).Select(p => p.Id));
        Assert.Contains("repair", FacilityMath.ProductsOf(sim.State, type, shop).Select(p => p.Id)); // the workshop is always on

        Assert.Null(Reject(sim, new RenaturalizeStructureCommand(shop.Id)));
        var big = Shop(sim, WithDemoCenter, departments: ["merch", "clothing", "protectors", "parts", "bikes"]);
        Assert.Equal(5, FacilityMath.ShelfSlots(type, big));
        Assert.Contains("No such building", Reject(sim, new SetShopDepartmentsCommand(shop.Id, ["merch"])));
    }

    [Fact]
    public void TheRange_ScalesPrices_Goods_Upkeep_AndWhoBuys()
    {
        var sim = Demo();
        var shop = Shop(sim);
        var type = FacilityMath.FindType(sim.State, shop.TypeId)!;
        var merch = FacilityMath.FindProduct(sim.State, "merch")!;
        var repair = FacilityMath.FindProduct(sim.State, "repair")!;
        long standardUpkeep = FacilityMath.Upkeep(sim.State, type, shop);
        Assert.Null(Reject(sim, new SetShopRangeCommand(shop.Id, ShopRange.Premium)));
        var premium = sim.State.FacilityRules.ShopRanges[(int)ShopRange.Premium];
        Assert.Equal(merch.ReferencePriceCents * premium.PriceFactorPermille / 1000, FacilityMath.PriceAt(sim.State, merch, shop));
        Assert.Equal(1000, FacilityMath.BuyFactorAt(sim.State, merch, shop)); // fair at its range
        Assert.Equal(repair.ReferencePriceCents, FacilityMath.PriceAt(sim.State, repair, shop)); // the workshop isn't ranged
        Assert.Equal(standardUpkeep * premium.UpkeepFactorPermille / 1000, FacilityMath.Upkeep(sim.State, type, shop));

        var expert = new Guest { Skill = 950, CashCents = 100_000, Happiness = 500 };
        var beginner = new Guest { Skill = 100 };
        Assert.Equal(premium.Groups.Expert, FacilityMath.GroupFactorPermille(sim.State, merch, shop, expert));
        Assert.Equal(premium.Groups.Beginner, FacilityMath.GroupFactorPermille(sim.State, merch, shop, beginner));

        long money = sim.State.Finance.MoneyCents;
        long paid = FacilityMath.Sell(sim.State, sim.State.Tick, shop, expert, merch);
        long goods = merch.ReferencePriceCents * merch.GoodsCostPermille / 1000 * premium.GoodsFactorPermille / 1000;
        Assert.Equal(merch.ReferencePriceCents * premium.PriceFactorPermille / 1000, paid);
        Assert.Equal(money + paid - goods, sim.State.Finance.MoneyCents);
        Assert.Equal(0, expert.OverpaidCents); // the premium price is fair for premium goods
        Assert.Equal(500, expert.Happiness);
        Assert.Contains("merch", expert.ShopItems);
        Assert.True(FacilityMath.HasBought(expert, merch));

        // Bikes are paid by card.
        var bikes = FacilityMath.FindProduct(sim.State, "bikes")!;
        var broke = new Guest { Skill = 900 };
        FacilityMath.Sell(sim.State, sim.State.Tick, shop, broke, bikes);
        Assert.Equal(0, broke.CashCents);
    }

    [Fact]
    public void ABudgetShop_SellsMoreToBeginners_APremiumOneMoreToExperts()
    {
        static (long Beginners, long Experts) Protectors(ShopRange range)
        {
            var sim = Demo(s => s.FacilityRules = new FacilityRules { RenterSharePermille = 0 });
            var shop = Shop(sim);
            Assert.Null(Reject(sim, new SetShopRangeCommand(shop.Id, range)));
            var protectors = FacilityMath.FindProduct(sim.State, "protectors")!;
            protectors.Groups = new GroupFactors();
            protectors.ImpulsePermille = 30;
            var buyers = new List<int>();
            sim.Events.Subscribe<ProductSold>(e => { if (e.ProductId == "protectors") buyers.Add(e.GuestId); });
            var skills = new Dictionary<int, int>();
            for (int t = 0; t < GameTime.MinutesPerDay; t++)
            {
                sim.Step();
                foreach (var g in sim.State.Guests) skills[g.Id] = g.Skill;
                sim.Events.Dispatch();
            }
            var rules = sim.State.ReputationRules;
            return (buyers.Count(id => skills[id] < rules.IntermediateFromSkill), buyers.Count(id => skills[id] >= rules.ExpertFromSkill));
        }
        var budget = Protectors(ShopRange.Budget);
        var premium = Protectors(ShopRange.Premium);
        Assert.True(budget.Beginners > premium.Beginners, $"beginners {budget.Beginners} vs {premium.Beginners}");
        Assert.True(premium.Experts > budget.Experts, $"experts {premium.Experts} vs {budget.Experts}");
    }

    [Fact]
    public void Clothing_SellsBetweenLaps_InTheRain()
    {
        static long Clothing(bool rain)
        {
            var sim = Demo(s =>
            {
                if (!rain) return;
                s.WeatherRules = new Weather.WeatherRules { RainPermille = 1000, RainMinHours = 10, RainMaxHours = 10 };
            });
            Shop(sim);
            sim.RunDays(2);
            return Sold(sim, "clothing");
        }
        long dry = Clothing(false), wet = Clothing(true);
        Assert.True(wet > dry, $"{wet} in the rain vs {dry} dry");
    }

    [Fact]
    public void Protectors_HalveTheSeriousShare_AndSellAtTheBikeShop()
    {
        var rules = new CrashRules();
        int plain = CrashMath.SeriousPermille(rules, CrashCause.Feature, jump: true, 600, 500);
        Assert.Equal(plain * rules.ProtectedSeriousPermille / 1000, CrashMath.SeriousPermille(rules, CrashCause.Feature, jump: true, 600, 500, protectedRider: true));

        var sim = Demo();
        Shop(sim);
        sim.RunDays(1);
        Assert.True(Sold(sim, "protectors") > 0);
    }

    [Fact]
    public void TearingDownTheBikeShop_SendsItsRentersHome()
    {
        var sim = Demo();
        var shop = Shop(sim, WithRental, bikes: 20);
        sim.RunTicks(11 * 60);
        var renters = sim.State.Guests.Where(g => g.RentalBikeId != 0).Select(g => g.Id).ToList();
        Assert.NotEmpty(renters);
        Assert.Null(Reject(sim, new RenaturalizeStructureCommand(shop.Id)));
        Assert.All(sim.State.Guests.Where(g => renters.Contains(g.Id)), g => Assert.Equal(0, g.RentalBikeId));
        sim.RunTicks(3 * 60);
        Assert.DoesNotContain(sim.State.Guests, g => renters.Contains(g.Id) && g.Activity == RiderActivity.Idle);
    }

    // ---------------------------------------------------------------- reviews of the building

    [Fact]
    public void APlaceReview_RatesValueAndService_OfThatBuildingOnly()
    {
        var fine = ReviewMath.Place(new FacilityVisit { FacilityId = 7, Products = ["merch"], SpentCents = 2500 }, 800);
        Assert.Equal((1000, 1000), (fine.Value, fine.Service));
        Assert.Equal(ReviewMath.PlaceStars(1000, 1000, 800), fine.StarsTenths);
        Assert.True(fine.StarsTenths >= 45);

        var ripOff = ReviewMath.Place(new FacilityVisit { FacilityId = 7, SpentCents = 4000, OverpaidCents = 1000 }, 800);
        Assert.Equal(500, ripOff.Value);
        var full = ReviewMath.Place(new FacilityVisit { FacilityId = 7, NoPlace = 2 }, 800);
        Assert.Equal(1000 - 2 * ReviewMath.NoPlaceServiceLoss, full.Service);
        var worn = ReviewMath.Place(new FacilityVisit { FacilityId = 7, RentalConditionPermille = 100, SpentCents = 3500 }, 800);
        Assert.Equal(370, worn.Service);
        var saved = ReviewMath.Place(new FacilityVisit { FacilityId = 7, NoPlace = 2, Repaired = true }, 800);
        Assert.Equal(900, saved.Service);
        Assert.True(ripOff.StarsTenths < fine.StarsTenths && full.StarsTenths < fine.StarsTenths && worn.StarsTenths < fine.StarsTenths);
    }

    [Fact]
    public void Reviews_SayWhatHappenedAtEachBuilding()
    {
        var sim = Demo(s => s.CrashRules.BikeOnlyPermille = 1000, collisions: true);
        sim.State.ReputationRules.Enabled = true;
        var shop = Shop(sim, WithRental, bikes: 20);
        sim.RunDays(1);
        var reviews = sim.State.Reputation.Reviews;
        Assert.NotEmpty(reviews);
        var atShop = reviews.Select(r => r.Places.FirstOrDefault(p => p.FacilityId == shop.Id)).OfType<PlaceReview>().ToList();
        Assert.NotEmpty(atShop);
        Assert.Contains(atShop, p => p.Products.Contains("rental") && p.RentalConditionPermille is > 0 and < 1000);
        Assert.Contains(atShop, p => p.Repaired);
        Assert.All(atShop, p => Assert.InRange(p.StarsTenths, 10, 50));
        Assert.All(reviews, r => Assert.All(r.FacilityIds, id => Assert.Contains(r.Places, p => p.FacilityId == id))); // bought there ⇒ a place review
        Assert.Equal(ReviewMath.AveragePlaceStars(reviews, shop.Id), (int)Math.Round(atShop.Average(p => p.StarsTenths), MidpointRounding.AwayFromZero));
        Assert.Equal(reviews.Count(r => r.Places.Any(p => p.FacilityId == shop.Id)), ReviewMath.Mentioning(sim.State.Reputation, facilityId: shop.Id).Count());
    }

    [Fact]
    public void ShippedBikeShop_Validates()
    {
        var types = ScenarioLoader.LoadFacilityCatalog(Path.Combine(TestWorlds.RepoRoot(), "data", "facility_types.json"));
        var shop = Assert.Single(types, t => t.Kind == FacilityKind.BikeShop);
        Assert.Empty(shop.Validate());
        Assert.True(shop.ShelfSlots > 0 && shop.Workbenches > 0 && shop.Products.Contains("repair"));
        Assert.Contains(shop.Upgrades, u => u.MaxFleet > 0 && u.Products.Contains("rental"));
        Assert.Contains(shop.Upgrades, u => u.EBikes);
        Assert.All(shop.Upgrades, u => Assert.False(string.IsNullOrEmpty(u.Look))); // every upgrade is a visible extension
        var products = ScenarioLoader.LoadProductCatalog(Path.Combine(TestWorlds.RepoRoot(), "data", "products.json"));
        Assert.True(products.Count(p => p.Department) > shop.ShelfSlots); // a choice to make
        Assert.Equal(3, new FacilityRules().ShopRanges.Count);
        Assert.Empty(new FacilityRules().Validate());
        Assert.True(Ledger.IsIncome(MoneyCategory.Rentals) && Ledger.IsIncome(MoneyCategory.Repairs) && !Ledger.IsIncome(MoneyCategory.BikeCosts));
    }
}
