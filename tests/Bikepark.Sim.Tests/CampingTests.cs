using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>Phase 9.2: the parking fee, and campers who stay a few nights at a campground.</summary>
public class CampingTests
{
    private static readonly PointCm CampSpot = new(11000, 92000); // demo_facilities.json: next to the valley parking

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

    private static Facility Camp(Simulation sim, int level = 0)
    {
        Assert.Null(Reject(sim, new BuildFacilityCommand("campground", CampSpot, new PointCm(CampSpot.X + 1000, CampSpot.Z), Level: level)));
        return sim.State.Facilities[^1];
    }

    // ---------------------------------------------------------------- parking fee

    [Fact]
    public void ParkingFee_IsSharedByTheCar_AndCountsWithTheEntryFee()
    {
        var sim = Demo();
        Assert.Contains("negative", Reject(sim, new SetParkingFeeCommand(-1)));
        Assert.Contains("cannot exceed", Reject(sim, new SetParkingFeeCommand(sim.State.Rules.MaxParkingFeeCents + 1)));
        Assert.Null(Reject(sim, new SetParkingFeeCommand(800)));
        long share = LiftMath.ParkingShareCents(sim.State, 800);
        Assert.Equal(800 * 1000 / sim.State.LiftRules.GuestsPerCarPermille, share);

        var arrived = new List<GuestArrived>();
        sim.Events.Subscribe<GuestArrived>(arrived.Add);
        sim.RunTicks(12 * 60);
        sim.Events.Dispatch();
        Assert.NotEmpty(arrived);
        Assert.Equal(arrived.Count * share, sim.State.Finance.TotalParkingCents);
        Assert.Equal(sim.State.Finance.TotalParkingCents, sim.State.Finance.Today(MoneyCategory.ParkingFees));
        Assert.All(sim.State.Guests.Where(g => g.CampgroundId == 0),
            g => Assert.Equal(sim.State.Park.EntryFeeCents + share, g.PaidEntryCents));

        // A dearer car park means fewer arrivals, like a dearer entrance.
        int free = GuestArrivalSystem.ExpectedArrivalsPermille(Demo().State, 11 * 60);
        int paid = GuestArrivalSystem.ExpectedArrivalsPermille(sim.State, 11 * 60);
        Assert.True(paid < free, $"{paid} vs {free}");
    }

    [Fact]
    public void ParkingFee_IsNotCharged_WithoutParkingLots()
    {
        var sim = TestWorlds.Run(1337, 0, [.. TestWorlds.DemoNetwork(), new(0, new SetParkingFeeCommand(1000))]);
        sim.RunTicks(12 * 60);
        Assert.Equal(1000, sim.State.Park.ParkingFeeCents);
        Assert.Equal(0, sim.State.Finance.TotalParkingCents);
    }

    // ---------------------------------------------------------------- campers

    [Fact]
    public void Campers_PayTheirNights_SleepAtTheCamp_AndTheNightIsStillSkippable()
    {
        var sim = Demo();
        var camp = Camp(sim);
        var night = FacilityMath.FindProduct(sim.State, "pitch")!;
        var type = FacilityMath.FindType(sim.State, "campground")!;
        sim.RunTicks(GameTime.MinutesPerDay - 1 - (int)sim.State.Tick); // to the day's last minute

        var campers = sim.State.Guests.Where(g => g.CampgroundId == camp.Id).ToList();
        Assert.NotEmpty(campers);
        Assert.True(campers.Count <= FacilityMath.Pitches(type, camp));
        Assert.All(campers, g => Assert.Equal(RiderActivity.Camping, g.Activity));
        Assert.True(sim.State.Stats.TotalCampers >= campers.Count);
        long nights = campers.Sum(g => (long)g.NightsLeft);
        Assert.True(sim.State.Finance.TotalCampingCents >= nights * night.ReferencePriceCents); // incl. campers who left early
        Assert.Equal(campers.Count, sim.State.Guests.Count); // everybody else went home
        Assert.True(ParkSchedule.IsQuiet(sim.State, sim.State.Tick)); // the night can be skipped
        Assert.Equal(DayPhase.Closed, ParkSchedule.Phase(sim.State, sim.State.Tick));
    }

    [Fact]
    public void Campers_WakeUpAtOpening_RestedWithANewDayTicket_AndRideAgain()
    {
        var sim = Demo();
        Camp(sim);
        sim.RunTicks(GameTime.MinutesPerDay + sim.State.Rules.OpenMinute - 1 - (int)sim.State.Tick); // just before day 2's opening
        var sleeping = sim.State.Guests.Where(g => g.Activity == RiderActivity.Camping).ToDictionary(g => g.Id, g => (g.NightsLeft, g.PaidEntryCents));
        Assert.NotEmpty(sleeping);
        long visitors = sim.State.Stats.TotalVisitors;
        var woke = new List<CamperWokeUp>();
        sim.Events.Subscribe<CamperWokeUp>(woke.Add);
        var arrived = new List<GuestArrived>();
        sim.Events.Subscribe<GuestArrived>(arrived.Add);
        sim.Events.Clear(); // only what happens from here

        sim.RunTicks(2);
        sim.Events.Dispatch();
        Assert.Equal(sleeping.Count, woke.Count);
        foreach (var guest in sim.State.Guests.Where(g => sleeping.ContainsKey(g.Id)))
        {
            Assert.NotEqual(RiderActivity.Camping, guest.Activity);
            Assert.Equal(sleeping[guest.Id].NightsLeft - 1, guest.NightsLeft);
            Assert.Equal(sleeping[guest.Id].PaidEntryCents + sim.State.Park.EntryFeeCents, guest.PaidEntryCents);
            Assert.False(guest.HasLiftPass);
            Assert.True(guest.Energy >= sim.State.FacilityRules.CampEnergyMin);
        }
        Assert.Equal(visitors + arrived.Count, sim.State.Stats.TotalVisitors); // campers are the same guests, not new visitors
        Assert.True(sim.State.Stats.VisitorsToday >= sleeping.Count);

        // During the day they ride.
        var ids = sleeping.Keys.ToHashSet();
        sim.RunTicks(3 * 60);
        Assert.Contains(sim.State.Guests, g => ids.Contains(g.Id) && g.RunsCompleted > 0);
    }

    [Fact]
    public void Campers_LeaveAfterTheirLastNight_WithOneReview()
    {
        var sim = Demo(s => s.FacilityRules = new FacilityRules { MinNights = 1, MaxNights = 1, CampChancePermille = 1000, ApresEndMinute = 0 });
        sim.State.ReputationRules.Enabled = true;
        Camp(sim);
        var left = new List<GuestLeft>();
        sim.Events.Subscribe<GuestLeft>(left.Add);
        var slept = new HashSet<int>();
        sim.Events.Subscribe<CamperSleeping>(e => slept.Add(e.GuestId));
        sim.RunDays(3);
        sim.Events.Dispatch();
        Assert.NotEmpty(slept);
        // Each camper left once, after one night (the evening of their second day at the latest).
        Assert.Contains(slept, id => sim.State.Guests.All(g => g.Id != id));
        foreach (int id in slept.Where(id => sim.State.Guests.All(g => g.Id != id)))
            Assert.Single(left, l => l.GuestId == id);
        Assert.All(sim.State.Guests, g => Assert.True(g.NightsLeft <= 1));
        Assert.Equal(left.Count, left.Select(l => l.GuestId).Distinct().Count());
        Assert.Equal(sim.State.Stats.TotalGuestsLeft, left.Count);
    }

    [Fact]
    public void WithoutAFreePitch_GuestsComeForTheDay()
    {
        var sim = Demo(s => s.FacilityRules = new FacilityRules { CampChancePermille = 1000 });
        var camp = Camp(sim);
        FacilityMath.FindType(sim.State, "campground")!.Pitches = 3;
        sim.RunTicks(12 * 60);
        Assert.Equal(3, FacilityMath.PitchesTaken(sim.State, camp));
        Assert.Contains(sim.State.Guests, g => g.CampgroundId == 0);
    }

    [Fact]
    public void DearNights_FewerCampers()
    {
        static long Campers(long? price)
        {
            var sim = Demo();
            Camp(sim);
            if (price is { } p) Assert.Null(Reject(sim, new SetProductPriceCommand("pitch", p)));
            sim.RunDays(1);
            return sim.State.Stats.TotalCampers;
        }
        long fair = Campers(null), dear = Campers(2_700);
        Assert.True(fair > 5, $"{fair} campers");
        Assert.True(dear * 3 < fair, $"{dear} vs {fair} campers");
    }

    [Fact]
    public void Showers_WakeCampersHappier()
    {
        var plain = Demo();
        var tent = Camp(plain);
        var showers = Demo();
        var deluxe = Camp(showers, level: 1);
        var type = FacilityMath.FindType(plain.State, "campground")!;
        Assert.Equal(0, FacilityMath.MorningMood(type, tent));
        Assert.Equal(type.Upgrades[0].MorningMood, FacilityMath.MorningMood(type, deluxe));
    }

    [Fact]
    public void TearingDownTheCampground_SendsCampersHome()
    {
        var sim = Demo();
        var camp = Camp(sim);
        sim.RunTicks(GameTime.MinutesPerDay - 1 - (int)sim.State.Tick);
        Assert.Contains(sim.State.Guests, g => g.Activity == RiderActivity.Camping);
        Assert.Null(Reject(sim, new RenaturalizeStructureCommand(camp.Id)));
        sim.RunTicks(2);
        Assert.DoesNotContain(sim.State.Guests, g => g.CampgroundId == camp.Id);
    }

    [Fact]
    public void Campers_SurviveASaveAtNight_AndRunDeterministically()
    {
        var sim = Demo();
        Camp(sim);
        sim.RunTicks(GameTime.MinutesPerDay + 2 * 60 - (int)sim.State.Tick); // 02:00 on day 2: campers asleep
        Assert.Contains(sim.State.Guests, g => g.Activity == RiderActivity.Camping);
        var loaded = new Simulation(SaveGame.Clone(sim.State));
        sim.RunTicks(10 * 60);
        loaded.RunTicks(10 * 60);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded.State));
    }

    [Fact]
    public void ShippedCampground_SellsNights()
    {
        var types = ScenarioLoader.LoadFacilityCatalog(Path.Combine(TestWorlds.RepoRoot(), "data", "facility_types.json"));
        var camp = types.Single(t => t.Kind == FacilityKind.Campground);
        Assert.True(camp.Pitches > 0);
        var products = ScenarioLoader.LoadProductCatalog(Path.Combine(TestWorlds.RepoRoot(), "data", "products.json"));
        Assert.Contains(products, p => p.Overnight && camp.Products.Contains(p.Id));
        Assert.True(Ledger.IsRegular(MoneyCategory.Camping) && Ledger.IsIncome(MoneyCategory.Camping));
        Assert.True(Ledger.IsIncome(MoneyCategory.ParkingFees) && !Ledger.IsIncome(MoneyCategory.CampUpkeep));
    }
}
