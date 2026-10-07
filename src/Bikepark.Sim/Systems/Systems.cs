using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>A unit of per-tick game logic. Systems are stateless; all state lives in <see cref="WorldState"/>.</summary>
public interface ISimSystem
{
    void Update(SimContext ctx);
}

internal sealed class ParkHoursSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        int minute = GameTime.MinuteOfDay(ctx.Tick);
        if (minute == ctx.State.Rules.OpenMinute)
            ctx.Publish(new ParkOpened(ctx.Tick));
        else if (minute == ctx.State.Rules.CloseMinute)
            ctx.Publish(new ParkClosed(ctx.Tick));
    }
}

/// <summary>
/// Spawns guests while the park is open, following the day's arrival profile. Arrival rate drops as the entry fee rises
/// above the reference fee. With parking lots, guests arrive by car and are turned away when the lots are full.
/// </summary>
internal sealed class GuestArrivalSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        if (!ParkSchedule.IsOpen(state, ctx.Tick))
            return;

        int expectedPermille = ExpectedArrivalsPermille(state, GameTime.MinuteOfDay(ctx.Tick));
        int arrivals = expectedPermille / 1000 + (ctx.Rng.ChancePermille(expectedPermille % 1000) ? 1 : 0);

        for (int i = 0; i < arrivals; i++)
            TryAdmitGuest(ctx);
    }

    internal static int ExpectedArrivalsPermille(WorldState state, int minuteOfDay)
    {
        var rules = state.Rules;
        long reference = Math.Max(1, rules.ReferenceEntryFeeCents);
        long feeFactor = 1000 + (reference - state.Park.EntryFeeCents) * 500 / reference;
        feeFactor = Math.Clamp(feeFactor, 100, 1500);
        return (int)(rules.BaseArrivalPermille * feeFactor / 1000 * ProfilePermille(rules.ArrivalProfile, minuteOfDay) / 1000);
    }

    /// <summary>The arrival profile at a minute of the day (1000 without a profile).</summary>
    public static int ProfilePermille(List<ArrivalPoint> profile, int minuteOfDay)
    {
        if (profile.Count == 0) return 1000;
        if (minuteOfDay <= profile[0].Minute) return profile[0].Permille;
        for (int i = 1; i < profile.Count; i++)
        {
            var (a, b) = (profile[i - 1], profile[i]);
            if (minuteOfDay <= b.Minute)
                return a.Permille + (b.Permille - a.Permille) * (minuteOfDay - a.Minute) / (b.Minute - a.Minute);
        }
        return profile[^1].Permille;
    }

    /// <summary>A lunch minute in what is left of the lunch window (triangular, peaked in its middle), or -1.</summary>
    private static int PlanLunch(SimContext ctx, int arrivalMinute)
    {
        var rules = ctx.State.Rules;
        if (!rules.HasLunch || arrivalMinute >= rules.LunchEndMinute) return -1;
        int from = Math.Max(rules.LunchStartMinute, arrivalMinute + 1);
        int span = rules.LunchEndMinute - from;
        if (span <= 0) return -1;
        return from + (ctx.Rng.NextInt(span) + ctx.Rng.NextInt(span)) / 2;
    }

    private static void TryAdmitGuest(SimContext ctx)
    {
        var state = ctx.State;
        long fee = state.Park.EntryFeeCents;
        long cash = ctx.Rng.Range(2_000, 8_001);

        if (LiftMath.ParkingCapacity(state) is { } capacity && state.Guests.Count >= capacity)
        {
            state.Stats.TotalTurnedAway++;
            state.Stats.TurnedAwayToday++;
            state.Stats.TotalTurnedAwayParkingFull++;
            ctx.Publish(new GuestTurnedAway(ctx.Tick, TurnAwayReason.ParkingFull));
            return;
        }

        if (cash < fee)
        {
            state.Stats.TotalTurnedAway++;
            state.Stats.TurnedAwayToday++;
            ctx.Publish(new GuestTurnedAway(ctx.Tick));
            return;
        }

        long reference = Math.Max(1, state.Rules.ReferenceEntryFeeCents);
        int feePenalty = (int)Math.Max(0, (fee - reference) * 200 / reference);

        int lunchMinute = PlanLunch(ctx, GameTime.MinuteOfDay(ctx.Tick));
        var guest = new Guest
        {
            LunchMinute = lunchMinute,
            Id = state.AllocateEntityId(),
            CashCents = cash - fee,
            Happiness = Math.Clamp(ctx.Rng.Range(550, 801) - feePenalty, 0, 1000),
            ArrivedTick = ctx.Tick,
            PlannedStayMinutes = ctx.Rng.Range(60, 361),
            // Skill is triangular around 500: most riders are intermediate.
            Skill = (ctx.Rng.Range(0, 1001) + ctx.Rng.Range(0, 1001)) / 2,
            Style = (RiderStyle)ctx.Rng.NextInt(3),
            Energy = ctx.Rng.Range(650, 1001),
            Activity = RiderActivity.Wandering,
        };

        state.Finance.Earn(fee);
        state.Guests.Add(guest);
        state.Stats.TotalVisitors++;
        state.Stats.VisitorsToday++;
        ctx.Publish(new GuestArrived(ctx.Tick, guest.Id));
    }
}

/// <summary>
/// Updates guests in the park: mood, spending, rest, and leaving. Riders only leave between laps or out of a lift
/// queue, never during lunch; tired riders go home. After closing, guests between laps go home; riders on a lap finish
/// it during the last rides (<see cref="ParkRules.LastRideMinutes"/>), then everyone left goes home. Waiting in a queue beyond the grace time costs mood; queuing and
/// riding a lift restore energy.
/// </summary>
internal sealed class GuestSystem : ISimSystem
{
    private const int SnackChancePermille = 8;
    private const int SnackHappinessBoost = 15;
    private const int CrowdingPenalty = 2;
    private const int UnhappyThreshold = 250;

    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        var guests = state.Guests;
        if (guests.Count == 0)
            return;

        bool closed = !ParkSchedule.GuestsAllowed(state, ctx.Tick);
        bool lastRides = ParkSchedule.IsLastRides(state, ctx.Tick);
        bool crowded = guests.Count > state.Rules.Capacity;

        // Iterate in list order and compact in place: deterministic and allocation-free.
        int write = 0;
        for (int read = 0; read < guests.Count; read++)
        {
            var guest = guests[read];
            GuestLeaveReason? leave = closed || lastRides && IsBetweenLaps(guest)
                ? GuestLeaveReason.ParkClosed
                : UpdateGuest(ctx, guest, crowded);

            if (leave is { } reason)
            {
                var stats = state.Stats;
                stats.TotalGuestsLeft++;
                stats.SumExitHappiness += guest.Happiness;
                if (reason == GuestLeaveReason.Unhappy)
                    stats.TotalLeftUnhappy++;
                ctx.Publish(new GuestLeft(ctx.Tick, guest.Id, reason));
            }
            else
                guests[write++] = guest;
            if (leave is not null && guest.Activity == RiderActivity.Queuing)
                LiftSystem.LeaveQueue(state, guest);
        }
        guests.RemoveRange(write, guests.Count - write);
    }

    /// <summary>Not on a lap: idle, having lunch or wandering (queuing riders are on a lap).</summary>
    private static bool IsBetweenLaps(Guest guest) =>
        guest.Activity is RiderActivity.Idle or RiderActivity.Eating or RiderActivity.Wandering;

    private static GuestLeaveReason? UpdateGuest(SimContext ctx, Guest guest, bool crowded)
    {
        var rules = ctx.State.Rules;
        var liftRules = ctx.State.LiftRules;
        bool onTheWay = guest.Activity is RiderActivity.Climbing or RiderActivity.Descending or RiderActivity.Walking or RiderActivity.OnLift;

        guest.Happiness += ctx.Rng.Range(-1, 2);
        if (crowded)
            guest.Happiness -= CrowdingPenalty;

        if (guest.Activity is RiderActivity.Queuing or RiderActivity.OnLift)
            guest.Energy = Math.Min(1000, guest.Energy + liftRules.RestEnergyPerMinute);
        if (guest.Activity == RiderActivity.Queuing && ctx.Tick - guest.QueueSinceTick >= liftRules.QueueGraceMinutes)
            guest.Happiness -= liftRules.QueueMoodPenaltyPerMinute;

        if (!onTheWay && guest.CashCents >= rules.SnackPriceCents && ctx.Rng.ChancePermille(SnackChancePermille))
        {
            guest.CashCents -= rules.SnackPriceCents;
            ctx.State.Finance.Earn(rules.SnackPriceCents);
            guest.Happiness += SnackHappinessBoost;
        }

        guest.Happiness = Math.Clamp(guest.Happiness, 0, 1000);

        if (onTheWay || guest.Activity == RiderActivity.Eating)
            return null;
        if (guest.Activity == RiderActivity.Idle && guest.Energy < ctx.State.TrailRules.TiredEnergy)
            return GuestLeaveReason.Tired;
        if (guest.Happiness < UnhappyThreshold)
            return GuestLeaveReason.Unhappy;
        if (ctx.Tick - guest.ArrivedTick >= guest.PlannedStayMinutes)
            return GuestLeaveReason.StayCompleted;
        return null;
    }
}

/// <summary>
/// Charges daily upkeep, crew wages and the bike access fees of lifts run by lift companies (the tier in effect that
/// day), and closes the books at the last minute of each day.
/// </summary>
internal sealed class FinanceSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        if (!GameTime.IsLastMinuteOfDay(ctx.Tick))
            return;

        var state = ctx.State;
        state.Finance.Spend(state.Rules.DailyUpkeepCents);
        long wages = state.Crew.Count * state.CrewRules.WagePerDayCents;
        if (wages > 0)
        {
            state.Finance.Spend(wages);
            state.Finance.TotalWagesCents += wages;
            state.Finance.WagesTodayCents += wages;
        }
        foreach (var lift in state.Lifts)
        {
            if (lift.BikeAccess is not { } access || LiftNetwork.FindOperator(state, lift.OperatorId) is not { } op) continue;
            if (access.TierIndex < 0 || access.TierIndex >= op.BikeAccessTiers.Count) continue;
            long fee = op.BikeAccessTiers[access.TierIndex].DailyFeeCents;
            state.Finance.Spend(fee);
            state.Finance.TotalLiftFeesCents += fee;
            state.Finance.LiftFeesTodayCents += fee;
        }

        var report = new DayReport(
            Day: GameTime.Day(ctx.Tick),
            Visitors: state.Stats.VisitorsToday,
            TurnedAway: state.Stats.TurnedAwayToday,
            RevenueCents: state.Finance.RevenueTodayCents,
            ExpensesCents: state.Finance.ExpensesTodayCents,
            MoneyCents: state.Finance.MoneyCents,
            LiftFeesCents: state.Finance.LiftFeesTodayCents,
            LiftRides: state.Lifts.Sum(l => l.Stats.RidersToday),
            WagesCents: state.Finance.WagesTodayCents,
            WoodStock: state.WoodStock,
            Jobs: state.Jobs.Count);
        ctx.Publish(new DayEnded(ctx.Tick, report));

        foreach (var way in state.Ways)
            way.Stats.RunsToday = 0;
        foreach (var lift in state.Lifts)
        {
            lift.Stats.RidersToday = 0;
            lift.Stats.MaxQueueToday = 0;
        }
        state.Finance.LiftFeesTodayCents = 0;
        state.Finance.WagesTodayCents = 0;
        state.Stats.VisitorsToday = 0;
        state.Stats.TurnedAwayToday = 0;
        state.Finance.RevenueTodayCents = 0;
        state.Finance.ExpensesTodayCents = 0;
    }
}
