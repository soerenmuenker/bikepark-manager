using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Safety;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Multi-day guests (see <see cref="FacilityRules"/>): campers sleep at their campground instead of going home while they
/// have nights left, and wake up at the next opening for another day in the park. Asleep they are not in the park
/// (<see cref="ParkSchedule.AwakeGuests"/>). Called by <see cref="GuestSystem"/>.
/// </summary>
internal static class Camping
{
    /// <summary>
    /// A camper done for the day goes to their pitch, if they have nights left and the campground still stands. True if
    /// they went to sleep (they don't leave).
    /// </summary>
    public static bool TryGoToSleep(SimContext ctx, Guest guest)
    {
        var state = ctx.State;
        if (guest.NightsLeft <= 0 || guest.Injury != InjurySeverity.None || state.Park.Bankrupt || Campground(state, guest) is null)
            return false;
        if (ctx.Network.HasBase) RiderSystem.PlaceAtBase(state, guest, ctx.Network); // off the lap and out of any queue
        else if (guest.Activity == RiderActivity.Queuing) LiftSystem.LeaveQueue(state, guest);
        guest.Route = [];
        guest.Activity = RiderActivity.Camping;
        guest.FacilityId = 0;
        guest.WakeTick = ParkSchedule.NextWakeTick(state, ctx.Tick);
        ctx.Publish(new CamperSleeping(ctx.Tick, guest.Id, guest.CampgroundId, guest.NightsLeft - 1));
        return true;
    }

    /// <summary>
    /// A minute of a camper's night. At <see cref="Guest.WakeTick"/> they wake up rested for a new day: a new day ticket
    /// (entry fee, lift pass), a new plan for the day; they go home if they can't pay it, the campground is gone or the
    /// park went bankrupt. Returns the reason they leave, or null.
    /// </summary>
    public static GuestLeaveReason? Night(SimContext ctx, Guest guest)
    {
        var state = ctx.State;
        if (state.Park.Bankrupt || Campground(state, guest) is not { } camp) return GuestLeaveReason.StayCompleted;
        if (ctx.Tick < guest.WakeTick) return null;

        guest.NightsLeft--;
        state.Stats.TotalCampNights++;
        long fee = state.Park.EntryFeeCents;
        if (guest.CashCents < fee) return GuestLeaveReason.StayCompleted;
        state.Finance.Earn(MoneyCategory.EntryFees, fee);
        guest.CashCents -= fee;
        guest.PaidEntryCents += fee;
        guest.HasLiftPass = false;

        var rules = state.FacilityRules;
        int morningMood = FacilityMath.FindType(state, camp.TypeId) is { } type ? FacilityMath.MorningMood(type, camp) : 0;
        guest.Energy = ctx.Rng.Range(rules.CampEnergyMin, rules.CampEnergyMax + 1);
        guest.Happiness = Math.Clamp(guest.Happiness + rules.CampMood + morningMood, 0, 1000);
        guest.Alcohol = 0;
        guest.CaffeineUntilTick = 0;
        guest.ArrivedTick = ctx.Tick;
        guest.PlannedStayMinutes = state.Rules.CloseMinute - state.Rules.OpenMinute;
        guest.HadLunch = false;
        guest.LunchMinute = GuestArrivalSystem.PlanLunch(ctx, GameTime.MinuteOfDay(ctx.Tick));
        guest.Activity = RiderActivity.Wandering; // placed at the base by the riders' system
        state.Stats.VisitorsToday++;
        ctx.Publish(new CamperWokeUp(ctx.Tick, guest.Id));
        return null;
    }

    private static Facility? Campground(WorldState state, Guest guest) =>
        guest.CampgroundId == 0 ? null : state.Facilities.FirstOrDefault(f => f.Id == guest.CampgroundId);
}
