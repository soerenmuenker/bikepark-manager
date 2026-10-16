using System.Text.Json.Nodes;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Safety;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>Phase 9.1: products at park-wide prices, upgrades, bars anywhere with gravel access, stops on the way, coffee, beer, après-bike.</summary>
public class FoodAndDrinkTests
{
    private static readonly PointCm BarSpot = new(17500, 83000); // demo_facilities.json: next to the gondola's valley station
    private static readonly PointCm RondelSpot = new(22000, 89500);
    private static readonly PointCm PathSpot = new(36743, 71370); // beside the hiking route, far from any station
    private static readonly PointCm TopSpot = new(58500, 48000); // beside the gondola's top station

    private static PointCm East(PointCm p) => new(p.X + 1000, p.Z);

    /// <summary>The Demo with its trails, rich, no collisions or reputation, after tick 0.</summary>
    private static Simulation Demo(Action<ScenarioDefinition>? tweak = null)
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.StartingMoneyCents = 100_000_000;
        tweak?.Invoke(scenario);
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

    private static Facility Build(Simulation sim, string typeId, PointCm at, int level = 0)
    {
        Assert.Null(Reject(sim, new BuildFacilityCommand(typeId, at, East(at), Level: level)));
        return sim.State.Facilities[^1];
    }

    private static ProductType Product(Simulation sim, string id) => FacilityMath.FindProduct(sim.State, id)!;

    // ---------------------------------------------------------------- content

    [Fact]
    public void ShippedCatalogs_Validate_AndEveryBuildingSellsKnownProducts()
    {
        var products = ScenarioLoader.LoadProductCatalog(Path.Combine(TestWorlds.RepoRoot(), "data", "products.json"));
        Assert.All(products, p => Assert.Empty(p.Validate()));
        Assert.Single(products, p => p.Lunch);
        Assert.Contains(products, p => p.Alcohol > 0);
        Assert.Contains(products, p => p.CaffeineMinutes > 0);
        var types = ScenarioLoader.LoadFacilityCatalog(Path.Combine(TestWorlds.RepoRoot(), "data", "facility_types.json"));
        foreach (var type in types)
            Assert.All(type.Products.Concat(type.Upgrades.SelectMany(u => u.Products)), id => Assert.Contains(products, p => p.Id == id));
        Assert.Contains(types, t => t.Id == "beer_rondel" && t.Products.Contains("beer"));
        Assert.All(types, t => Assert.NotEmpty(t.Upgrades));
        // Both scenarios load them (validation checks the product references).
        Assert.NotEmpty(ScenarioLoader.CreateWorld(TestWorlds.CareerScenario()).ProductTypes);
        Assert.NotEmpty(ScenarioLoader.CreateWorld(TestWorlds.LiftScenario()).ProductTypes);
    }

    [Fact]
    public void Scenario_WithAnUnknownProduct_IsRejected()
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.FacilityTypes[0].Products.Add("caviar");
        var error = Assert.Throws<InvalidDataException>(() => ScenarioLoader.CreateWorld(scenario));
        Assert.Contains("unknown product 'caviar'", error.Message);
    }

    // ---------------------------------------------------------------- prices

    [Fact]
    public void BuyFactor_FallsAbovetheReferencePrice_RisesBelowIt_Capped()
    {
        var beer = new ProductType { Id = "beer", ReferencePriceCents = 500, MaxPriceCents = 1500, ElasticityPermille = 600 };
        Assert.Equal(1000, FacilityMath.BuyFactorPermille(beer, 500));
        Assert.Equal(400, FacilityMath.BuyFactorPermille(beer, 1000)); // double price: 60 % fewer buyers
        Assert.Equal(0, FacilityMath.BuyFactorPermille(beer, 1500));
        Assert.Equal(1300, FacilityMath.BuyFactorPermille(beer, 250));
        Assert.Equal(1500, FacilityMath.BuyFactorPermille(beer, 0)); // capped
        var rules = new FacilityRules { RipOffMood = 30 };
        Assert.Equal(0, FacilityMath.RipOffMood(rules, beer, 400));
        Assert.Equal(30, FacilityMath.RipOffMood(rules, beer, 1000));
    }

    [Fact]
    public void ProductPrices_AreSetParkWide_WithinTheirLimits()
    {
        var sim = Demo();
        var lunch = Product(sim, "lunch");
        Assert.Equal(lunch.ReferencePriceCents, FacilityMath.PriceOf(sim.State, lunch));
        Assert.Contains("Unknown product", Reject(sim, new SetProductPriceCommand("caviar", 100)));
        Assert.Contains("negative", Reject(sim, new SetProductPriceCommand("lunch", -1)));
        Assert.Contains("cannot cost more", Reject(sim, new SetProductPriceCommand("lunch", lunch.MaxPriceCents + 1)));
        Assert.Null(Reject(sim, new SetProductPriceCommand("lunch", 1800)));
        Assert.Null(Reject(sim, new SetProductPriceCommand("lunch", 2000)));
        Assert.Equal(2000, FacilityMath.PriceOf(sim.State, lunch));
        Assert.Single(sim.State.Park.Prices);
    }

    [Fact]
    public void ExpensiveLunch_SellsLess_AndGuestsFeelRippedOff()
    {
        (int AtBar, long Overpaid, long Revenue) Day(long? price)
        {
            var sim = Demo();
            var bar = Build(sim, "snack_bar", BarSpot);
            if (price is { } p) Assert.Null(Reject(sim, new SetProductPriceCommand("lunch", p)));
            var lunches = new List<GuestAteLunch>();
            sim.Events.Subscribe<GuestAteLunch>(lunches.Add);
            long overpaid = 0;
            for (int t = 0; t < GameTime.MinutesPerDay; t++)
            {
                sim.Step();
                overpaid = Math.Max(overpaid, sim.State.Guests.Sum(g => g.OverpaidCents));
            }
            sim.Events.Dispatch();
            var atBar = lunches.Where(l => l.FacilityId == bar.Id).ToList();
            if (price is { } paid) Assert.All(atBar, l => Assert.Equal(paid, l.PaidCents));
            return (atBar.Count, overpaid, atBar.Sum(l => l.PaidCents));
        }

        var fair = Day(null);
        var steep = Day(2800); // double the reference: 80 % elasticity → one in five accepts
        Assert.True(fair.AtBar > 20, $"{fair.AtBar} lunches at the fair price");
        Assert.True(steep.AtBar * 3 < fair.AtBar, $"{steep.AtBar} vs {fair.AtBar} lunches");
        Assert.Equal(0, fair.Overpaid);
        Assert.True(steep.Overpaid > 0);
    }

    [Fact]
    public void Overpaying_LowersTheValueOfTheReview()
    {
        var rules = new Reputation.ReputationRules();
        var guest = new Guest { RunsCompleted = 4, PaidEntryCents = 1500, VisitFunSum = 2000, ArrivedTick = 0 };
        int fair = Reputation.ReviewMath.Score(rules, guest, 300).Value;
        guest.OverpaidCents = 1500;
        Assert.True(Reputation.ReviewMath.Score(rules, guest, 300).Value < fair);
    }

    // ---------------------------------------------------------------- upgrades

    [Fact]
    public void Upgrades_AddProductsAndSeats_CostMoneyAndUpkeep_InOrder()
    {
        var sim = Demo();
        var bar = Build(sim, "snack_bar", BarSpot);
        var type = FacilityMath.FindType(sim.State, "snack_bar")!;
        Assert.False(FacilityMath.Sells(type, bar, "coffee"));

        long money = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new UpgradeFacilityCommand(bar.Id)));
        Assert.Equal(1, bar.Level);
        Assert.True(FacilityMath.Sells(type, bar, "coffee"));
        Assert.Equal(money - type.Upgrades[0].CostCents, sim.State.Finance.MoneyCents);
        Assert.Equal($"{bar.Name}: {type.Upgrades[0].Name}", sim.State.Finance.Transactions[^1].Label);

        Assert.Null(Reject(sim, new UpgradeFacilityCommand(bar.Id)));
        Assert.Equal(type.Seats + type.Upgrades[1].Seats, FacilityMath.Seats(type, bar));
        Assert.Contains("fully upgraded", Reject(sim, new UpgradeFacilityCommand(bar.Id)));
        Assert.Contains("No such building", Reject(sim, new UpgradeFacilityCommand(999)));

        var days = new List<DayReport>();
        sim.Events.Subscribe<DayEnded>(e => days.Add(e.Report));
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.Equal(type.UpkeepPerDayCents + type.Upgrades.Sum(u => u.UpkeepPerDayCents), days.Single().FacilityUpkeepCents);

        // Torn down: the contractor's share of everything paid for it.
        long before = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new RenaturalizeStructureCommand(bar.Id)));
        Assert.Equal(before - FacilityMath.InvestedCents(type, bar) * sim.State.LiftRules.RemovalPermille / 1000, sim.State.Finance.MoneyCents);
    }

    [Fact]
    public void Upgrades_NeedTheirParkLevel_AndTheMoney()
    {
        var gated = Demo(s => s.Rules.UnlockAll = false);
        FacilityMath.FindType(gated.State, "snack_bar")!.Upgrades[1].RequiredLevel = Reputation.ReputationRules.MaxLevel; // the terrace: out of reach
        var bar = Build(gated, "snack_bar", BarSpot);
        Assert.Null(Reject(gated, new UpgradeFacilityCommand(bar.Id))); // the espresso machine: level 0
        Assert.Contains("needs park level", Reject(gated, new UpgradeFacilityCommand(bar.Id)));
        Assert.Contains("needs park level", Reject(gated, new BuildFacilityCommand("snack_bar", PathSpot, East(PathSpot), Level: 2)));

        var poor = Demo(s => s.StartingMoneyCents = 900_000);
        var cheap = Build(poor, "snack_bar", BarSpot);
        Assert.Contains("Not enough money", Reject(poor, new UpgradeFacilityCommand(cheap.Id)));
    }

    [Fact]
    public void Building_WithLevel_ComesWithItsUpgrades_AndPaysForThem()
    {
        var sim = Demo();
        var type = FacilityMath.FindType(sim.State, "snack_bar")!;
        long money = sim.State.Finance.MoneyCents;
        var bar = Build(sim, "snack_bar", BarSpot, level: 1);
        Assert.Equal(1, bar.Level);
        Assert.Equal(money - type.BuildCostCents - type.Upgrades[0].CostCents, sim.State.Finance.MoneyCents);
        Assert.Contains("upgrades", Reject(sim, new BuildFacilityCommand("snack_bar", PathSpot, East(PathSpot), Level: 9)));
    }

    // ---------------------------------------------------------------- where

    [Fact]
    public void Bars_CanStandAnywhere_WithAGravelPathOrHubNearby()
    {
        var sim = Demo();
        Assert.Contains("gravel path", Reject(sim, new BuildFacilityCommand("snack_bar", new PointCm(50_000, 50_000), new PointCm(51_000, 50_000))));
        var path = Build(sim, "snack_bar", PathSpot); // beside the hiking route, no station anywhere near
        var top = Build(sim, "beer_rondel", TopSpot);
        Assert.Equal(2, sim.State.Facilities.Count);
        Assert.NotEqual(path.Id, top.Id);
        // A guest idle on the hiking route next to it is served by it; one at the valley base is not.
        var guest = new Guest { Activity = RiderActivity.Idle, LocationWayId = sim.State.Ways.First(w => w.Kind == WayKind.AccessPath).Id };
        var geometry = sim.Network.Geometry(guest.LocationWayId);
        guest.LocationCm = Enumerable.Range(0, (int)(geometry.LengthCm / 100)).Select(m => m * 100L)
            .MinBy(cm => Math.Abs(geometry.PositionAt(cm).X - PathSpot.X) + Math.Abs(geometry.PositionAt(cm).Z - PathSpot.Z));
        var served = FacilityMath.InReach(sim.State, FacilityMath.GuestPoint(sim.State, sim.Network, guest), FacilityKind.Bar);
        Assert.Equal(path.Id, Assert.Single(served).Facility.Id);
        var valley = new Guest { Activity = RiderActivity.Idle, LocationHubId = sim.Network.BaseHub!.Id };
        Assert.Empty(FacilityMath.InReach(sim.State, FacilityMath.GuestPoint(sim.State, sim.Network, valley), FacilityKind.Bar));
    }

    [Fact]
    public void RidersStop_AtABarOnTheWay_AndGoOnWithTheirLap()
    {
        var sim = Demo();
        var top = Build(sim, "snack_bar", TopSpot, level: 1); // at the gondola's top: lunch, coffee, snacks
        var sold = new List<ProductSold>();
        sim.Events.Subscribe<ProductSold>(sold.Add);
        var resumed = new HashSet<int>();
        var paused = new Dictionary<int, int>();
        for (int t = 0; t < GameTime.MinutesPerDay; t++)
        {
            sim.Step();
            foreach (var g in sim.State.Guests)
            {
                if (g.Activity == RiderActivity.Eating && g.ResumeLeg >= 0) paused[g.Id] = g.ResumeLeg;
                else if (paused.ContainsKey(g.Id) && g.Activity is RiderActivity.Descending or RiderActivity.Climbing) resumed.Add(g.Id);
            }
        }
        sim.Events.Dispatch();
        Assert.Contains(sold, s => s.FacilityId == top.Id && s.ProductId == "lunch");
        Assert.True(paused.Count > 5, $"{paused.Count} sat down on the way");
        Assert.True(resumed.Count * 2 > paused.Count, $"{resumed.Count} of {paused.Count} went on riding");
        Assert.True(top.Customers > paused.Count); // plus take-away snacks and coffee
    }

    // ---------------------------------------------------------------- coffee

    [Fact]
    public void Coffee_GivesEnergy_AndCaffeineHalvesTheClimb()
    {
        var sim = Demo();
        var bar = Build(sim, "snack_bar", BarSpot, level: 1);
        var guest = new Guest { Id = 9999, Energy = 400, Happiness = 500, CashCents = 5000 };
        long price = FacilityMath.Sell(sim.State, sim.State.Tick, bar, guest, Product(sim, "coffee"));
        Assert.Equal(Product(sim, "coffee").ReferencePriceCents, price);
        Assert.Equal(550, guest.Energy);
        Assert.Equal(sim.State.Tick + 60, guest.CaffeineUntilTick);
        Assert.Equal(price, sim.State.Finance.Today(MoneyCategory.Drinks));
        Assert.True(sim.State.Finance.Today(MoneyCategory.FoodGoods) > 0);

        // Riders pedal up the hiking route (no bikes on the gondola); caffeinated ones lose less energy.
        // Energy lost per minute of climbing (riders who don't tire ride more laps, so totals differ).
        static double LostPerMinute(bool caffeine)
        {
            var world = TestWorlds.RunLift(1337, 0, [.. TestWorlds.DemoLiftNetwork(), new(0, new SetLiftBikeAccessCommand(1, 0))]);
            world.State.CrashRules.CollisionDistanceCm = 0;
            long lost = 0, minutes = 0;
            for (int t = 0; t < 12 * 60; t++)
            {
                var before = world.State.Guests.Where(g => g.Activity == RiderActivity.Climbing).ToDictionary(g => g.Id, g => g.Energy);
                if (caffeine)
                    foreach (var g in world.State.Guests) g.CaffeineUntilTick = long.MaxValue;
                world.Step();
                foreach (var g in world.State.Guests)
                    if (before.TryGetValue(g.Id, out int e) && g.Activity == RiderActivity.Climbing)
                    {
                        lost += Math.Max(0, e - g.Energy);
                        minutes++;
                    }
            }
            return (double)lost / Math.Max(1, minutes);
        }
        double plain = LostPerMinute(false), boosted = LostPerMinute(true);
        Assert.True(plain > 0);
        Assert.True(boosted < plain * 0.7, $"{boosted:0.0} vs {plain:0.0} energy per climbing minute");
    }

    // ---------------------------------------------------------------- beer

    [Fact]
    public void Alcohol_RaisesCrashChances_AndMakesRidersBolder()
    {
        var rules = new CrashRules();
        Assert.Equal(1000, CrashMath.AlcoholFactor(rules, 0));
        Assert.Equal(1000 + rules.AlcoholFactorPermille / 2, CrashMath.AlcoholFactor(rules, 500));
        var sober = new Guest { Skill = 400, Energy = 1000 };
        var tipsy = new Guest { Skill = 400, Energy = 1000, Alcohol = 500 };
        var segment = new WaySegment(0, 0, 1000, 0, 0, 0, 0, 0, default, 500, TerrainDifficulty: 500);
        Assert.True(CrashMath.TerrainChancePpb(rules, tipsy, segment, 0) > CrashMath.TerrainChancePpb(rules, sober, segment, 0));

        var state = new WorldState();
        Assert.Equal(400, RiderSystem.PickingSkill(state, sober));
        Assert.Equal(400 + 500 * state.FacilityRules.BoldnessPermille / 1000, RiderSystem.PickingSkill(state, tipsy));

        // A tipsy rider wants the hard trail more than they should (Red Rocket vs Blue Line in the demo network).
        var sim = TestWorlds.Run(1337, 0, TestWorlds.DemoNetwork());
        sim.Step();
        var hard = sim.Network.Trails.MaxBy(t => sim.Network.Geometry(t.Id).DifficultyScore)!;
        int soberWeight = RiderSystem.Weight(sober, hard, sim.Network.Geometry(hard.Id), RiderSystem.PickingSkill(state, sober));
        int boldWeight = RiderSystem.Weight(tipsy, hard, sim.Network.Geometry(hard.Id), RiderSystem.PickingSkill(state, tipsy));
        Assert.True(boldWeight > soberWeight, $"{boldWeight} vs {soberWeight}");
    }

    [Fact]
    public void Alcohol_WearsOff_AndTooMuchSendsGuestsHome()
    {
        var sim = Demo();
        sim.RunTicks(10 * 60 - 1); // 10:00: guests in the park
        var guest = sim.State.Guests.First(g => g.Activity != RiderActivity.Injured);
        guest.Alcohol = 300;
        sim.RunTicks(10);
        Assert.Equal(300 - 10 * sim.State.FacilityRules.AlcoholDecayPerMinute, guest.Alcohol);

        var left = new List<GuestLeft>();
        sim.Events.Subscribe<GuestLeft>(left.Add);
        foreach (var g in sim.State.Guests) g.Alcohol = 1000;
        sim.RunTicks(120);
        sim.Events.Dispatch();
        Assert.Contains(left, l => l.Reason == GuestLeaveReason.Drunk);
    }

    [Fact]
    public void BeerHours_KeepAlcoholFromRiders_ButNotFromAprèsBike()
    {
        var sim = Demo();
        Build(sim, "beer_rondel", RondelSpot);
        Assert.Contains("within", Reject(sim, new SetBeerHoursCommand(-1)));
        Assert.Null(Reject(sim, new SetBeerHoursCommand(GameTime.MinutesPerDay))); // never to riders
        var apres = new HashSet<int>();
        sim.Events.Subscribe<ApresStarted>(e => apres.Add(e.GuestId));
        var drinks = new List<ProductSold>();
        sim.Events.Subscribe<ProductSold>(e => { if (e.ProductId is "beer" or "radler") drinks.Add(e); });
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.NotEmpty(apres);
        Assert.NotEmpty(drinks);
        Assert.All(drinks, d => Assert.Contains(d.GuestId, apres));
    }

    [Fact]
    public void AprèsBike_GuestsStayAtTheBar_UntilTheEveningEnds_ThenTheParkIsEmpty()
    {
        var sim = Demo();
        var rondel = Build(sim, "beer_rondel", RondelSpot);
        var type = FacilityMath.FindType(sim.State, rondel.TypeId)!;
        int end = sim.State.FacilityRules.ApresEndMinute;
        Assert.True(end > sim.State.Rules.CloseMinute);
        var started = new List<ApresStarted>();
        sim.Events.Subscribe<ApresStarted>(started.Add);
        int most = 0, afterClose = 0;
        for (int t = 0; t < GameTime.MinutesPerDay; t++)
        {
            sim.Step();
            most = Math.Max(most, FacilityMath.SeatsTaken(sim.State, rondel));
            int minute = GameTime.MinuteOfDay(sim.State.Tick);
            if (minute > sim.State.Rules.CloseMinute && minute < end)
                afterClose = Math.Max(afterClose, sim.State.Guests.Count(g => g.Activity == RiderActivity.Apres));
            if (minute >= end)
                Assert.DoesNotContain(sim.State.Guests, g => g.Activity == RiderActivity.Apres);
        }
        sim.Events.Dispatch();
        Assert.True(started.Count > 10, $"{started.Count} stayed for après-bike");
        Assert.True(afterClose > 0, "nobody stayed after closing");
        Assert.True(most <= FacilityMath.Seats(type, rondel));
        Assert.Equal(started.Count, sim.State.Stats.TotalApresGuests);
        Assert.All(started, s => Assert.True(GameTime.MinuteOfDay(s.UntilTick) <= end));
        Assert.True(ParkSchedule.IsQuiet(sim.State, sim.State.Tick));
    }

    [Fact]
    public void AprèsBike_IsOff_WithoutAnEndMinute()
    {
        var sim = Demo(s => s.FacilityRules.ApresEndMinute = 0);
        Build(sim, "beer_rondel", RondelSpot);
        sim.RunDays(1);
        Assert.Equal(0, sim.State.Stats.TotalApresGuests);
    }

    // ---------------------------------------------------------------- saves

    [Fact]
    public void Food_SurvivesASave_AndRunsDeterministically()
    {
        var sim = Demo();
        foreach (var c in TestWorlds.DemoFacilities()) sim.Commands.Enqueue(c.Command);
        sim.Commands.Enqueue(new SetProductPriceCommand("beer", 650));
        sim.RunTicks(16 * 60);
        Assert.Equal(3, sim.State.Facilities.Count);
        var loaded = new Simulation(SaveGame.Clone(sim.State));
        sim.RunTicks(300);
        loaded.RunTicks(300);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded.State));
    }

    [Fact]
    public void Load_MigratesVersion8_FacilityPricesToProducts()
    {
        var sim = Demo();
        foreach (var c in TestWorlds.DemoFacilities().Take(2)) sim.Commands.Enqueue(c.Command);
        sim.RunTicks(12 * 60);
        var root = JsonNode.Parse(SaveGame.Serialize(sim.State))!.AsObject();
        root["version"] = 8;
        var world = root["world"]!.AsObject();
        // The v8 shape: prices and goods on the building types, buildings belong to a lift, no products or upgrades.
        foreach (var type in world["facilityTypes"]!.AsArray().OfType<JsonObject>())
        {
            string id = type["id"]!.GetValue<string>();
            type.Remove("products");
            type.Remove("upgrades");
            if (type["kind"]!.GetValue<string>() == "bar")
            {
                type["lunchPriceCents"] = id == "bike_cafe" ? 1800 : 1400;
                type["snackPriceCents"] = 400;
            }
            else
            {
                type["merchPriceCents"] = 2500;
                type["merchCostPermille"] = 450;
            }
            type["buyChancePermille"] = id == "merch_shop" ? 120 : 0;
        }
        world["facilityTypes"] = new JsonArray(world["facilityTypes"]!.AsArray().OfType<JsonObject>()
            .Where(t => t["id"]!.GetValue<string>() is "snack_bar" or "merch_shop" or "bike_cafe").Select(t => (JsonNode)t.DeepClone()).ToArray());
        foreach (var facility in world["facilities"]!.AsArray().OfType<JsonObject>())
        {
            facility.Remove("level");
            facility["liftId"] = 1;
        }
        world.Remove("productTypes");
        world.Remove("facilityRules");
        world["park"]!.AsObject().Remove("prices");
        world["park"]!.AsObject().Remove("beerFromMinute");

        var loaded = SaveGame.Deserialize(root.ToJsonString());
        Assert.Equal(["lunch", "snack", "merch"], loaded.ProductTypes.Select(p => p.Id));
        Assert.Equal(1400, loaded.ProductTypes[0].ReferencePriceCents); // the first building's price
        Assert.True(loaded.ProductTypes[0].Lunch);
        Assert.Equal(450, loaded.ProductTypes.Single(p => p.Id == "merch").GoodsCostPermille);
        Assert.Equal(["lunch", "snack"], loaded.FacilityTypes.Single(t => t.Id == "snack_bar").Products);
        Assert.All(loaded.Facilities, f => Assert.Equal(0, f.Level));
        var running = new Simulation(loaded);
        running.RunDays(1);
        Assert.True(running.State.Finance.TotalFoodCents > sim.State.Finance.TotalFoodCents);
    }
}
