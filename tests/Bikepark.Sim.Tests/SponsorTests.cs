using Bikepark.Sim.Banking;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.Sponsors;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

/// <summary>Phase 9.4: trail and park sponsors (applications, offers, signs, payments, benefits) and photo spots.</summary>
public class SponsorTests
{
    /// <summary>The Demo with its trails (rich, no collisions), after tick 0.</summary>
    private static Simulation Demo(Action<ScenarioDefinition>? tweak = null)
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.StartingMoneyCents = 100_000_000;
        scenario.StartingWood = 200;
        tweak?.Invoke(scenario);
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
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

    private static Way Trail(Simulation sim, string name) => sim.State.Ways.Single(w => w.Name == name);

    /// <summary>Runs to the opening of the next day.</summary>
    private static void ToNextOpening(Simulation sim)
    {
        long next = (GameTime.Day(sim.State.Tick) + 1) * GameTime.MinutesPerDay + sim.State.Rules.OpenMinute;
        sim.RunTicks(next - sim.State.Tick + 1);
    }

    /// <summary>A running deal with its sign up (as if offered, accepted and built).</summary>
    private static Sponsorship Running(Simulation sim, string brandId, int wayId)
    {
        var deal = new Sponsorship
        {
            Id = sim.State.AllocateEntityId(), SponsorId = brandId, WayId = wayId, Status = SponsorshipStatus.Active, SignBuilt = true,
            EndDay = GameTime.Day(sim.State.Tick) + 30,
        };
        sim.State.Sponsorships.Add(deal);
        return deal;
    }

    // ---------------------------------------------------------------- content

    [Fact]
    public void ShippedBrands_Validate_AndBothScenariosHaveThem()
    {
        var brands = ScenarioLoader.LoadSponsorCatalog(Path.Combine(TestWorlds.RepoRoot(), "data", "sponsors.json"));
        Assert.All(brands, b => Assert.Empty(b.Validate()));
        Assert.Contains(brands, b => b.SponsorsParks);
        Assert.Contains(brands, b => b.Benefits.Any(x => x.Kind == SponsorBenefitKind.BikeDiscount));
        Assert.Contains(brands, b => b.Benefits.Any(x => x.Kind == SponsorBenefitKind.GoodsDiscount && x.Targets.Contains("beer")));
        Assert.All(brands.Where(b => b.SponsorsParks), b => Assert.True(b.ParkDailyCents > b.DailyCents * 3, $"{b.Name}: a park deal weighs more"));
        Assert.NotEmpty(ScenarioLoader.CreateWorld(TestWorlds.CareerScenario()).SponsorTypes);
        Assert.NotEmpty(Demo().State.SponsorTypes);
    }

    // ---------------------------------------------------------------- applying, offers, deals

    [Fact]
    public void Applying_IsAnsweredAfterTheDays_YesForAFittingTrail_NoWithTheReason()
    {
        var sim = Demo();
        sim.RunDays(1); // the trails get their first riders
        var trail = Trail(sim, "Flow Country");
        var answers = new List<SponsorAnswered>();
        sim.Events.Subscribe<SponsorAnswered>(answers.Add);
        Assert.Null(Reject(sim, new ApplySponsorCommand("valley_cycles", trail.Id)));
        Assert.Contains("already talking", Reject(sim, new ApplySponsorCommand("valley_cycles", trail.Id)));
        Assert.Contains("Unknown sponsor", Reject(sim, new ApplySponsorCommand("acme", trail.Id)));
        // Bergbräu wants far more traffic than any trail has.
        SponsorMath.Find(sim.State, "bergbraeu")!.MinRunsPerDay = 100_000;
        Assert.Null(Reject(sim, new ApplySponsorCommand("bergbraeu", Trail(sim, "Red Rocket").Id)));

        ToNextOpening(sim);
        Assert.Empty(answers); // not yet: answered after two days
        ToNextOpening(sim);
        sim.Events.Dispatch();
        Assert.Equal(2, answers.Count);
        Assert.Contains(answers, a => a.Yes);
        Assert.Contains(answers, a => !a.Yes && a.Reason.Contains("runs a day"));
        Assert.Equal(SponsorshipStatus.Offered, SponsorMath.Open(sim.State, "valley_cycles")!.Status);
    }

    [Fact]
    public void AcceptingAnOffer_RenamesTheTrail_TheCrewPutsUpTheSign_ThenItPays()
    {
        var sim = Demo();
        var trail = Trail(sim, "Flow Country");
        var deal = SponsorSystemOffer(sim, "valley_cycles", trail.Id);
        Assert.Null(Reject(sim, new HireCrewCommand()));
        Assert.Null(Reject(sim, new HireCrewCommand()));
        Assert.Null(Reject(sim, new AcceptSponsorCommand(deal.Id)));
        Assert.Equal("Valley Cycles Flow Country", trail.Name);
        Assert.Equal("Flow Country", deal.OriginalName);
        Assert.Contains(sim.State.Jobs, j => j.Kind == Crew.JobKind.BuildSign && j.FeatureId == deal.Id);
        Assert.Contains("sponsor's name", Reject(sim, new RenameTrailCommand(trail.Id, "My Trail")));

        // No money before the sign stands.
        sim.RunTicks(sim.State.Rules.OpenMinute - (int)(sim.State.Tick % GameTime.MinutesPerDay) - 1);
        Assert.False(deal.SignBuilt);
        Assert.Equal(0, sim.State.Finance.TotalSponsorCents);
        sim.RunTicks(GameTime.MinutesPerDay - (int)(sim.State.Tick % GameTime.MinutesPerDay)); // through the day
        Assert.True(deal.SignBuilt);
        var brand = SponsorMath.Find(sim.State, "valley_cycles")!;
        Assert.True(deal.PaidCents >= brand.DailyCents);
        Assert.Equal(deal.PaidCents, sim.State.Finance.TotalSponsorCents);
    }

    [Fact]
    public void Deals_EndAtTheirLastDay_AndTheTrailGetsItsNameBack()
    {
        var sim = Demo();
        var trail = Trail(sim, "Flow Country");
        var deal = SponsorSystemOffer(sim, "valley_cycles", trail.Id);
        Assert.Null(Reject(sim, new AcceptSponsorCommand(deal.Id)));
        deal.EndDay = GameTime.Day(sim.State.Tick);
        var ended = new List<SponsorshipEnded>();
        sim.Events.Subscribe<SponsorshipEnded>(ended.Add);
        sim.RunTicks(GameTime.MinutesPerDay - (int)(sim.State.Tick % GameTime.MinutesPerDay));
        sim.Events.Dispatch();
        Assert.Single(ended);
        Assert.Equal(SponsorshipStatus.Ended, deal.Status);
        Assert.Equal("Flow Country", trail.Name);
        Assert.DoesNotContain(sim.State.Jobs, j => j.Kind == Crew.JobKind.BuildSign); // the unbuilt sign's job went with it
    }

    [Fact]
    public void ATrailClosedTooLong_EndsItsDeal()
    {
        var sim = Demo();
        var trail = Trail(sim, "Flow Country");
        var deal = Running(sim, "valley_cycles", trail.Id);
        Assert.Null(Reject(sim, new SetTrailClosedCommand(trail.Id, true)));
        sim.RunDays(sim.State.SponsorRules.ClosedDaysAllowed + 1);
        Assert.Equal(SponsorshipStatus.Ended, deal.Status);
        Assert.Contains("closed too long", deal.Note);
    }

    [Fact]
    public void Brands_OfferDealsByThemselves_OnlyForGoodTrails()
    {
        static List<SponsorOffered> Offers(Action<WorldState> tweak)
        {
            var sim = Demo(s => s.SponsorRules = new SponsorRules { OfferCheckDays = 1, OfferChancePermille = 1000 });
            tweak(sim.State);
            var offers = new List<SponsorOffered>();
            sim.Events.Subscribe<SponsorOffered>(offers.Add);
            sim.RunDays(3);
            sim.Events.Dispatch();
            return offers;
        }
        var good = Offers(_ => { });
        Assert.NotEmpty(good);
        Assert.All(good, o => Assert.Equal(o.SponsorshipId, o.SponsorshipId));
        var none = Offers(state =>
        {
            foreach (var brand in state.SponsorTypes)
            {
                brand.MinStarsTenths = 50;
                brand.ParkDailyCents = 0; // no park deals either
            }
        });
        Assert.Empty(none);
    }

    [Fact]
    public void ThePark_HasOneSponsorAtATime_ThatPaysMore_AndNamesThePark()
    {
        var sim = Demo();
        var gravity = SponsorMath.Find(sim.State, "gravity")!;
        Assert.Null(Reject(sim, new ApplySponsorCommand("gravity", 0)));
        Assert.Contains("already has a sponsor", Reject(sim, new ApplySponsorCommand("bolt", 0)));
        Assert.Contains("doesn't sponsor whole parks", Reject(sim, new ApplySponsorCommand("valley_cycles", 0)));

        var park = Running(sim, "bergbraeu", 0);
        Assert.Equal($"{sim.State.Park.Name} presented by Bergbräu", SponsorMath.ParkTitle(sim.State));
        var brand = SponsorMath.Find(sim.State, "bergbraeu")!;
        Assert.True(SponsorMath.Payment(sim.State, brand, park) > SponsorMath.Payment(sim.State, brand, Running(sim, "bergbraeu", Trail(sim, "Red Rocket").Id)));
        Assert.True(gravity.ParkDailyCents > gravity.DailyCents);
    }

    // ---------------------------------------------------------------- benefits

    [Fact]
    public void Benefits_CountInFullForThePark_InPartForATrail_TheBestWins_AndStopWithTheDeal()
    {
        var sim = Demo();
        var rental = FacilityMath.FindType(sim.State, "bike_shop")!;
        Assert.Equal(rental.BikeCostCents, FacilityMath.BikeCost(sim.State, rental));

        var trailDeal = Running(sim, "gravity", Trail(sim, "Red Rocket").Id);
        int trailShare = 400 * sim.State.SponsorRules.TrailBenefitPermille / 1000;
        Assert.Equal(rental.BikeCostCents - rental.BikeCostCents * trailShare / 1000, FacilityMath.BikeCost(sim.State, rental));

        var parkDeal = Running(sim, "gravity", 0);
        Assert.Equal(rental.BikeCostCents - rental.BikeCostCents * 400 / 1000, FacilityMath.BikeCost(sim.State, rental)); // the best one

        parkDeal.SignBuilt = false; // no sign, no benefit
        trailDeal.Status = SponsorshipStatus.Ended;
        Assert.Equal(rental.BikeCostCents, FacilityMath.BikeCost(sim.State, rental));
    }

    [Fact]
    public void Benefits_ApplyWhereTheMoneyIsSpent()
    {
        var sim = Demo();
        var state = sim.State;
        Running(sim, "bergbraeu", 0); // beer goods −60 %
        var beer = FacilityMath.FindProduct(state, "beer")!;
        var guest = new Guest { Id = 9999, CashCents = 10_000 };
        long goodsBefore = state.Finance.Today(MoneyCategory.FoodGoods);
        FacilityMath.Sell(state, state.Tick, null, guest, beer);
        long full = beer.ReferencePriceCents * beer.GoodsCostPermille / 1000;
        Assert.Equal(full - full * 600 / 1000, state.Finance.Today(MoneyCategory.FoodGoods) - goodsBefore);

        Running(sim, "summit", 0); // campground −30 %
        var camp = FacilityMath.FindType(state, "campground")!;
        Assert.Equal(camp.BuildCostCents * 700 / 1000, FacilityMath.BuildCost(state, camp));

        Running(sim, "talbank", 0); // loan interest −50 %
        var offer = state.LoanRules.Offers[0];
        Assert.Equal(state.LoanRules.SponsorInterestPermille, LoanMath.InterestPermille(state, sim.Network));
        Assert.True(LoanMath.Terms(state, sim.Network, offer).Total < offer.PrincipalCents * (1000 + state.LoanRules.WorstInterestPermille) / 1000);

        Running(sim, "steelrope", 0); // lifts −30 %
        var gondola = state.LiftTypes.First();
        Assert.Equal(gondola.BuildCostCents - gondola.BuildCostCents * 300 / 1000, LiftWorks.BuildCost(state, gondola));

        // A car maker pays half again on every parking fee.
        Running(sim, "alpen_motors", 0);
        Assert.Null(Reject(sim, new SetParkingFeeCommand(1000)));
        sim.RunTicks(12 * 60);
        Assert.True(state.Finance.TotalParkingCents > 0);
        Assert.True(state.Finance.TotalSponsorCents >= state.Finance.TotalParkingCents / 2 - 100);
    }

    // ---------------------------------------------------------------- photo spots

    [Fact]
    public void Cameras_FilmJumps_AndRidersBuyTheirClips()
    {
        var sim = Demo();
        var trail = Trail(sim, "Flow Country");
        Assert.Null(Reject(sim, new PlaceTrailFeatureCommand(trail.Id, "table", 22_000, Instant: true)));
        Assert.Null(Reject(sim, new PlaceTrailFeatureCommand(trail.Id, "berm", 4_000, Instant: true)));
        var table = trail.Features.Single(f => f.TypeId == "table");
        var berm = trail.Features.Single(f => f.TypeId == "berm");
        Assert.Contains("jumps and drops", Reject(sim, new PlaceCameraCommand(trail.Id, berm.Id)));
        long money = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new PlaceCameraCommand(trail.Id, table.Id)));
        Assert.Equal(money - sim.State.SponsorRules.CameraCostCents, sim.State.Finance.MoneyCents);
        Assert.Contains("already", Reject(sim, new PlaceCameraCommand(trail.Id, table.Id)));

        var clips = new List<ClipSold>();
        sim.Events.Subscribe<ClipSold>(clips.Add);
        sim.RunTicks(10 * 60);
        sim.Events.Dispatch();
        Assert.NotEmpty(clips);
        Assert.Equal(clips.Count, table.ClipsSold);
        Assert.Equal(clips.Sum(c => c.PriceCents), sim.State.Finance.TotalClipCents);
        Assert.True(sim.State.Reputation.BuzzPermille > 0);
        Assert.True(sim.State.Reputation.BuzzPermille <= sim.State.SponsorRules.MaxBuzzPermille);

        Assert.Null(Reject(sim, new RemoveCameraCommand(trail.Id, table.Id)));
        Assert.False(table.Camera);
    }

    [Fact]
    public void DearClips_SellLess()
    {
        static int Clips(long? price)
        {
            var sim = Demo();
            var trail = Trail(sim, "Flow Country");
            Assert.Null(Reject(sim, new PlaceTrailFeatureCommand(trail.Id, "table", 22_000, Instant: true)));
            var table = trail.Features.Single(f => f.TypeId == "table");
            Assert.Null(Reject(sim, new PlaceCameraCommand(trail.Id, table.Id)));
            if (price is { } p) Assert.Null(Reject(sim, new SetProductPriceCommand("clip", p)));
            sim.RunDays(1);
            return table.ClipsSold;
        }
        int fair = Clips(null), dear = Clips(2_200);
        Assert.True(fair > 5, $"{fair} clips");
        Assert.True(dear * 3 < fair, $"{dear} vs {fair} clips");
    }

    [Fact]
    public void Deals_SurviveASave_AndRunDeterministically()
    {
        var sim = Demo(s => s.SponsorRules = new SponsorRules { OfferCheckDays = 1, OfferChancePermille = 1000 });
        sim.RunDays(2);
        Assert.NotEmpty(sim.State.Sponsorships);
        var loaded = new Simulation(SaveGame.Clone(sim.State));
        sim.RunTicks(GameTime.MinutesPerDay);
        loaded.RunTicks(GameTime.MinutesPerDay);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded.State));
    }

    /// <summary>A brand's offer for a trail (or the park), as the brands' system makes it.</summary>
    private static Sponsorship SponsorSystemOffer(Simulation sim, string brandId, int wayId)
    {
        var deal = new Sponsorship
        {
            Id = sim.State.AllocateEntityId(), SponsorId = brandId, WayId = wayId, Status = SponsorshipStatus.Offered,
            ExpiresTick = sim.State.Tick + 7 * GameTime.MinutesPerDay,
        };
        sim.State.Sponsorships.Add(deal);
        return deal;
    }
}
