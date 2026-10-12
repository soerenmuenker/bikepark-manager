using Bikepark.Sim.Core;
using Bikepark.Sim.Facilities;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.Safety;
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
        if (ctx.State.Park.Bankrupt) return;
        if (minute == ctx.State.Rules.OpenMinute)
            ctx.Publish(new ParkOpened(ctx.Tick));
        else if (minute == ctx.State.Rules.CloseMinute)
            ctx.Publish(new ParkClosed(ctx.Tick));
    }
}

/// <summary>
/// Spawns guests while the park is open, following the day's arrival profile and weather. Arrival rate drops as the entry fee rises
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
        long weather = state.WeatherRules.ArrivalPermille(state.Weather.Today.Kind);
        long reputation = ReputationMath.DemandPermille(state);
        return (int)(rules.BaseArrivalPermille * feeFactor / 1000 * ProfilePermille(rules.ArrivalProfile, minuteOfDay) / 1000 * weather / 1000
                     * reputation / 1000);
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
            PaidEntryCents = fee,
            Happiness = Math.Clamp(ctx.Rng.Range(550, 801) - feePenalty, 0, 1000),
            ArrivedTick = ctx.Tick,
            PlannedStayMinutes = ctx.Rng.Range(60, 361),
            // Skill is triangular around 500: most riders are intermediate.
            Skill = (ctx.Rng.Range(0, 1001) + ctx.Rng.Range(0, 1001)) / 2,
            Style = (RiderStyle)ctx.Rng.NextInt(3),
            Energy = ctx.Rng.Range(650, 1001),
            Activity = RiderActivity.Wandering,
        };

        if (state.Reputation.InfluencerDue)
        {
            // The influencer rides like a good rider and stays the day.
            state.Reputation.InfluencerDue = false;
            guest.IsInfluencer = true;
            guest.Skill = state.ReputationRules.InfluencerSkill;
            guest.PlannedStayMinutes = state.ReputationRules.InfluencerStayMinutes;
        }

        state.Finance.Earn(fee);
        state.Guests.Add(guest);
        state.Stats.TotalVisitors++;
        state.Stats.VisitorsToday++;
        ctx.Publish(new GuestArrived(ctx.Tick, guest.Id));
        if (guest.IsInfluencer)
            ctx.Publish(new InfluencerArrived(ctx.Tick, guest.Id, ReputationMath.InfluencerName(guest.Id)));
    }
}

/// <summary>
/// Updates guests in the park: mood, spending, rest, and leaving. Riders only leave between laps or out of a lift
/// queue, never during lunch; tired riders go home. After closing (the last rides), guests between laps and in lift queues go home;
/// riders on a lift or a trail finish their lap, then they go home too. Waiting in a queue beyond the grace time costs mood; queuing and
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
        bool raining = Weather.WeatherMath.IsRaining(state, ctx.Tick);

        // Iterate in list order and compact in place: deterministic and allocation-free.
        int write = 0;
        for (int read = 0; read < guests.Count; read++)
        {
            var guest = guests[read];
            // A seriously injured rider stays where they fell (even after closing) until the helicopter has flown them out.
            GuestLeaveReason? leave = guest.Activity == RiderActivity.Injured
                ? ctx.Tick >= guest.RescueAtTick ? GuestLeaveReason.Evacuated : null
                : closed || lastRides && IsBetweenLaps(guest)
                    ? GuestLeaveReason.ParkClosed
                    : UpdateGuest(ctx, guest, crowded, raining);
            if (leave == GuestLeaveReason.Evacuated)
            {
                state.Safety.Evacuations++;
                state.Safety.SumRescueMinutes += ctx.Tick - guest.CrashTick;
                ctx.Publish(new RiderEvacuated(ctx.Tick, guest.Id, guest.CrashWayId));
            }

            if (leave is { } reason)
            {
                var stats = state.Stats;
                stats.TotalGuestsLeft++;
                stats.SumExitHappiness += guest.Happiness;
                if (reason == GuestLeaveReason.Unhappy)
                    stats.TotalLeftUnhappy++;
                if (reason is not (GuestLeaveReason.Evacuated or GuestLeaveReason.Injured))
                    BuyMerch(ctx, guest);
                ctx.Publish(new GuestLeft(ctx.Tick, guest.Id, reason));
                ReputationSystem.Review(ctx, guest);
            }
            else
                guests[write++] = guest;
            if (leave is not null && guest.Activity == RiderActivity.Queuing)
                LiftSystem.LeaveQueue(state, guest);
        }
        guests.RemoveRange(write, guests.Count - write);
    }

    /// <summary>
    /// Between laps a guest now and then buys a snack: at a bar at their base (scenarios with bars), or anywhere at the
    /// park's snack price (scenarios without).
    /// </summary>
    private static void TrySnack(SimContext ctx, Guest guest)
    {
        var state = ctx.State;
        if (!FacilityMath.BarsNeeded(state))
        {
            if (guest.CashCents < state.Rules.SnackPriceCents || !ctx.Rng.ChancePermille(SnackChancePermille)) return;
            guest.CashCents -= state.Rules.SnackPriceCents;
            state.Finance.Earn(state.Rules.SnackPriceCents);
            guest.Happiness += SnackHappinessBoost;
            return;
        }
        var bar = FacilityMath.At(state, FacilityMath.BaseLiftOf(state, ctx.Network, guest), FacilityKind.Bar).FirstOrDefault();
        if (bar.Facility is null || bar.Type.SnackPriceCents <= 0 || guest.CashCents < bar.Type.SnackPriceCents) return;
        if (!ctx.Rng.ChancePermille(SnackChancePermille)) return;
        FacilityMath.Sell(state, bar.Facility, guest, bar.Type.SnackPriceCents);
        state.Finance.TotalSnackCents += bar.Type.SnackPriceCents;
        state.Finance.FoodTodayCents += bar.Type.SnackPriceCents;
        guest.Happiness += SnackHappinessBoost;
    }

    /// <summary>
    /// A guest going home passes the shop at their base (or the arrival base) and buys something with the shop's chance,
    /// scaled by their mood. The goods cost the park <see cref="FacilityType.MerchCostPermille"/> of the price.
    /// </summary>
    private static void BuyMerch(SimContext ctx, Guest guest)
    {
        var state = ctx.State;
        if (state.Facilities.Count == 0) return;
        int liftId = FacilityMath.BaseLiftOf(state, ctx.Network, guest);
        if (liftId == 0 && ctx.Network.BaseHub is { } hub)
            liftId = state.ParkingLots.FirstOrDefault(p => p.Id == hub.Id)?.LiftId ?? state.Lifts.FirstOrDefault(l => l.Valley.Id == hub.Id)?.Id ?? 0;
        var shop = FacilityMath.At(state, liftId, FacilityKind.Shop).FirstOrDefault();
        if (shop.Facility is null || guest.CashCents < shop.Type.MerchPriceCents || shop.Type.MerchPriceCents <= 0) return;
        if (!ctx.Rng.ChancePermille(shop.Type.BuyChancePermille * Math.Clamp(guest.Happiness, 0, 1000) / 1000)) return;
        long price = shop.Type.MerchPriceCents;
        FacilityMath.Sell(state, shop.Facility, guest, price);
        shop.Facility.Customers++;
        state.Finance.TotalMerchCents += price;
        state.Finance.MerchTodayCents += price;
        long cost = price * shop.Type.MerchCostPermille / 1000;
        state.Finance.Spend(cost);
        state.Finance.TotalMerchCostCents += cost;
        ctx.Publish(new MerchSold(ctx.Tick, guest.Id, shop.Facility.Id, price));
    }

    /// <summary>On a lift whose cabins keep riders dry (T-bars and chairlifts don't).</summary>
    internal static bool OnShelteredLift(WorldState state, Guest guest)
    {
        if (guest.Route.Count == 0) return true;
        int liftId = guest.Route[Math.Clamp(guest.LegIndex, 0, guest.Route.Count - 1)].WayId;
        var lift = state.Lifts.FirstOrDefault(l => l.Id == liftId);
        return lift is null || LiftNetwork.FindType(state, lift.TypeId) is not { Sheltered: false };
    }

    /// <summary>Not on the way up or down: idle, having lunch, wandering or waiting in a lift queue.</summary>
    private static bool IsBetweenLaps(Guest guest) =>
        guest.Activity is RiderActivity.Idle or RiderActivity.Eating or RiderActivity.Wandering or RiderActivity.Queuing;

    private static GuestLeaveReason? UpdateGuest(SimContext ctx, Guest guest, bool crowded, bool raining)
    {
        var rules = ctx.State.Rules;
        var liftRules = ctx.State.LiftRules;
        bool onTheWay = guest.Activity is RiderActivity.Climbing or RiderActivity.Descending or RiderActivity.Walking or RiderActivity.OnLift;

        guest.Happiness += ctx.Rng.Range(-1, 2);
        if (crowded)
            guest.Happiness -= CrowdingPenalty;

        if (raining && !(guest.Activity == RiderActivity.OnLift && OnShelteredLift(ctx.State, guest)))
            guest.Happiness -= ctx.State.WeatherRules.RainMoodPerMinute;
        if (guest.Activity is RiderActivity.Queuing or RiderActivity.OnLift)
            guest.Energy = Math.Min(1000, guest.Energy + liftRules.RestEnergyPerMinute);
        if (guest.Activity == RiderActivity.Queuing && ctx.Tick - guest.QueueSinceTick >= liftRules.QueueGraceMinutes)
        {
            guest.Happiness -= liftRules.QueueMoodPenaltyPerMinute;
            guest.QueueMinutes++;
        }

        if (!onTheWay)
            TrySnack(ctx, guest);

        guest.Happiness = Math.Clamp(guest.Happiness, 0, 1000);

        if (onTheWay || guest.Activity == RiderActivity.Eating)
            return null;
        if (guest.Injury != InjurySeverity.None)
            return GuestLeaveReason.Injured; // down at last: home to recover
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
/// Charges daily upkeep, trail and feature upkeep, crew wages and the bike access fees of lifts run by lift companies (the tier in effect that
/// day), and closes the books at the last minute of each day: days in the red are counted towards bankruptcy.
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
        long facilityUpkeep = state.Facilities.Sum(f => FacilityMath.FindType(state, f.TypeId)?.UpkeepPerDayCents ?? 0);
        if (facilityUpkeep > 0)
        {
            state.Finance.Spend(facilityUpkeep);
            state.Finance.TotalFacilityUpkeepCents += facilityUpkeep;
        }
        long trailUpkeep = Trails.TrailFeatures.DailyUpkeepCents(state, ctx.Network);
        if (trailUpkeep > 0)
        {
            state.Finance.Spend(trailUpkeep);
            state.Finance.TotalTrailUpkeepCents += trailUpkeep;
            state.Finance.TrailUpkeepTodayCents += trailUpkeep;
        }
        foreach (var lift in state.Lifts)
        {
            if (lift.OperatorId is null && lift.InService && LiftNetwork.FindType(state, lift.TypeId) is { UpkeepPerDayCents: > 0 } own)
            {
                state.Finance.Spend(own.UpkeepPerDayCents);
                state.Finance.TotalLiftUpkeepCents += own.UpkeepPerDayCents;
                state.Finance.LiftUpkeepTodayCents += own.UpkeepPerDayCents;
            }
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
            Jobs: state.Jobs.Count,
            Weather: state.Weather.Today.Kind,
            RainMinutes: state.Weather.Today.RainEndMinute - state.Weather.Today.RainStartMinute,
            TrailsClosed: state.Ways.Count(w => w.Kind == Trails.WayKind.Trail && w.Built && !w.IsRideable),
            Reviews: state.Reputation.ReviewsToday,
            RatingTenths: ReputationMath.RatingTenths(state),
            DemandPermille: ReputationMath.DemandPermille(state),
            Xp: ParkProgress.Xp(state, ctx.Network).Total,
            Level: state.Reputation.Level,
            Crashes: state.Safety.History.LastOrDefault(d => d.Day == GameTime.Day(ctx.Tick)) is { } accidents ? accidents.Minor + accidents.Serious : 0,
            SeriousCrashes: state.Safety.History.LastOrDefault(d => d.Day == GameTime.Day(ctx.Tick))?.Serious ?? 0,
            InsuranceCents: state.Safety.InsuranceTodayCents,
            LiftUpkeepCents: state.Finance.LiftUpkeepTodayCents,
            LiftTicketsCents: state.Finance.LiftTicketsTodayCents,
            LoanPaymentsCents: state.Finance.LoanPaymentsTodayCents,
            DebtCents: Banking.LoanMath.DebtCents(state),
            DaysInDebt: CountDebtDays(ctx),
            TrailUpkeepCents: state.Finance.TrailUpkeepTodayCents,
            FoodCents: state.Finance.FoodTodayCents,
            MerchCents: state.Finance.MerchTodayCents,
            FacilityUpkeepCents: facilityUpkeep);
        ctx.Publish(new DayEnded(ctx.Tick, report));
        state.DayHistory.Add(report);
        if (state.DayHistory.Count > WorldState.DayHistoryDays)
            state.DayHistory.RemoveRange(0, state.DayHistory.Count - WorldState.DayHistoryDays);

        foreach (var way in state.Ways)
            way.Stats.RunsToday = 0;
        foreach (var lift in state.Lifts)
        {
            lift.Stats.RidersToday = 0;
            lift.Stats.MaxQueueToday = 0;
        }
        state.Finance.LiftFeesTodayCents = 0;
        state.Finance.LiftTicketsTodayCents = 0;
        state.Finance.LoanPaymentsTodayCents = 0;
        state.Finance.TrailUpkeepTodayCents = 0;
        state.Finance.FoodTodayCents = 0;
        state.Finance.MerchTodayCents = 0;
        foreach (var facility in state.Facilities)
            facility.RevenueTodayCents = 0;
        state.Finance.WagesTodayCents = 0;
        state.Stats.VisitorsToday = 0;
        state.Reputation.ReviewsToday = 0;
        state.Safety.InsuranceTodayCents = 0;
        state.Finance.LiftUpkeepTodayCents = 0;
        state.Stats.TurnedAwayToday = 0;
        state.Finance.RevenueTodayCents = 0;
        state.Finance.ExpensesTodayCents = 0;
    }

    /// <summary>
    /// Counts the days the books close in the red (reset by a day in the black), warns on the first day, a week before
    /// and the day before, and declares the park bankrupt at <see cref="ParkRules.BankruptcyDays"/>.
    /// </summary>
    private static int CountDebtDays(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Finance.MoneyCents >= 0)
            return state.Finance.DaysInDebt = 0;
        int days = ++state.Finance.DaysInDebt;
        int limit = state.Rules.BankruptcyDays;
        if (limit <= 0 || state.Park.Bankrupt) return days;
        int left = limit - days;
        if (left <= 0)
        {
            state.Park.Bankrupt = true;
            ctx.Publish(new ParkBankrupt(ctx.Tick, days));
        }
        else if (days == 1 || left == 7 || left == 1)
        {
            ctx.Publish(new DebtWarning(ctx.Tick, days, left));
        }
        return days;
    }
}
