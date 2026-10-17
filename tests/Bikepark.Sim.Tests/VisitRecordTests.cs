using System.Text.Json.Nodes;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Reputation;

namespace Bikepark.Sim.Tests;

/// <summary>Reviews remember the visit; buildings and lifts keep their own stats (for the inspector windows and the review browser).</summary>
public class VisitRecordTests
{
    private static Simulation Park(long days) =>
        TestWorlds.RunLift(1337, days * 1440, [.. TestWorlds.DemoLiftNetwork(), .. TestWorlds.DemoFacilities(), new(0, new SetLiftTicketCommand(1000))],
            s => s.StartingMoneyCents = 100_000_000);

    private static Review At(SkillGroup group, int stars, int fun, int safety = 0, int trail = 0, int lift = 0, int facility = 0) =>
        new(0, 1, group, stars, fun, safety, 0, 0, 0, 0)
        {
            TrailIds = trail == 0 ? [] : [trail], LiftIds = lift == 0 ? [] : [lift], FacilityIds = facility == 0 ? [] : [facility],
        };

    [Fact]
    public void Reviews_RememberTrailsLiftsBuildingsMoneyAndWhy()
    {
        var sim = Park(3);
        var state = sim.State;
        var reviews = state.Reputation.Reviews;
        Assert.NotEmpty(reviews);
        Assert.All(reviews, r =>
        {
            Assert.Equal(r.Day, Core.GameTime.Day(r.LeftTick));
            Assert.True(r.TrailIds.Count <= ReviewMath.MaxTrails);
            Assert.All(r.TrailIds, id => Assert.Contains(state.Ways, w => w.Id == id));
            Assert.All(r.LiftIds, id => Assert.Contains(state.Lifts, l => l.Id == id));
            Assert.All(r.FacilityIds, id => Assert.Contains(state.Facilities, f => f.Id == id));
            Assert.True(r.SpentCents >= 0);
        });
        Assert.Contains(reviews, r => r.TrailIds.Count > 0);
        Assert.Contains(reviews, r => r.LiftIds.Count > 0);
        Assert.Contains(reviews, r => r.FacilityIds.Count > 0);
        Assert.Contains(reviews, r => r.SpentCents > 0);
        Assert.Contains(reviews, r => r.Reason is GuestLeaveReason.ParkClosed or GuestLeaveReason.StayCompleted or GuestLeaveReason.Tired);
    }

    [Fact]
    public void AspectAverages_OverAllAGroupOrAMention()
    {
        Review[] reviews =
        [
            At(SkillGroup.Beginner, 40, 800, 1000, trail: 5),
            At(SkillGroup.Beginner, 20, 200, 500, trail: 6, lift: 1),
            At(SkillGroup.Expert, 30, 501, 0, facility: 9, lift: 1),
        ];
        var (all, n) = ReviewMath.AspectAverages(reviews);
        Assert.Equal(3, n);
        Assert.Equal(500, all.Fun); // (800 + 200 + 501) / 3 = 500.33
        Assert.Equal(500, all.Safety);
        Assert.Equal(500, ReviewMath.AspectAverages(reviews.Where(r => r.Group == SkillGroup.Beginner)).Average.Fun);
        Assert.Equal((default(ReviewScores), 0), ReviewMath.AspectAverages([]));
        Assert.Equal(30, ReviewMath.AverageStars(reviews));
        Assert.Null(ReviewMath.AverageStars([]));

        var state = new ReputationState { Reviews = [.. reviews] };
        Assert.Equal(2, ReviewMath.Mentioning(state, liftId: 1).Count());
        Assert.Single(ReviewMath.Mentioning(state, trailId: 5));
        Assert.Single(ReviewMath.Mentioning(state, facilityId: 9));
        Assert.Empty(ReviewMath.Mentioning(state, trailId: 99));
    }

    [Fact]
    public void Buildings_CountTheirOwnSalesPerProduct()
    {
        var sim = Park(2);
        var state = sim.State;
        Assert.Contains(state.Facilities, f => f.Customers > 0);
        foreach (var facility in state.Facilities)
        {
            Assert.Equal(facility.Customers, facility.Sales.Sum(s => s.Sold));
            Assert.Equal(facility.TotalRevenueCents, facility.Sales.Sum(s => s.RevenueCents));
            Assert.Equal(facility.RevenueTodayCents, facility.Sales.Sum(s => s.RevenueTodayCents));
            Assert.Equal(facility.CustomersToday, facility.Sales.Sum(s => s.SoldToday));
            Assert.True(facility.TurnedAwayToday <= facility.TurnedAway);
        }
        // Each building's product sales add up to (at most) the park's.
        foreach (var park in state.Finance.ProductSales)
            Assert.True(state.Facilities.Sum(f => f.Sales.Where(s => s.ProductId == park.ProductId).Sum(s => s.Sold)) <= park.Sold);
    }

    [Fact]
    public void Lifts_CountTheDayPassesSoldThere()
    {
        var sim = Park(2);
        var state = sim.State;
        Assert.True(state.Finance.TotalLiftTicketsCents > 0);
        Assert.Equal(state.Finance.TotalLiftTicketsCents, state.Lifts.Sum(l => l.Stats.PassRevenueCents));
        Assert.Equal(state.Lifts.Sum(l => l.Stats.PassesSold) * 1000, state.Lifts.Sum(l => l.Stats.PassRevenueCents));
    }

    [Fact]
    public void Load_MigratesVersion9_GuestsGetTheMoneyTheyCameWith()
    {
        var sim = Park(1);
        sim.RunTicks(10 * 60); // day 2, 10:00: guests in the park
        var root = JsonNode.Parse(SaveGame.Serialize(sim.State))!.AsObject();
        root["version"] = 9;
        var world = root["world"]!.AsObject();
        foreach (var guest in world["guests"]!.AsArray().OfType<JsonObject>())
            foreach (string key in new[] { "broughtCents", "trailRuns", "facilitiesUsed", "liftsUsed" }) guest.Remove(key);
        foreach (var review in world["reputation"]!["reviews"]!.AsArray().OfType<JsonObject>())
            foreach (string key in new[] { "leftTick", "trailIds", "facilityIds", "liftIds", "spentCents", "reason", "crashed" }) review.Remove(key);

        var loaded = SaveGame.Deserialize(root.ToJsonString());
        Assert.NotEmpty(loaded.Guests);
        Assert.All(loaded.Guests, g => Assert.Equal(g.CashCents + g.PaidEntryCents, g.BroughtCents));
        Assert.All(loaded.Reputation.Reviews, r => Assert.Empty(r.TrailIds));
        var running = new Simulation(loaded);
        running.RunDays(1);
        Assert.Contains(running.State.Reputation.Reviews, r => r.TrailIds.Count > 0);
    }
}
