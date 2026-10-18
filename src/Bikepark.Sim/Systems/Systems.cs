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
/// Spawns guests while the park is open, following the day's arrival profile and weather. Arrival rate drops as the arrival cost
/// (entry fee plus the guest's share of the parking fee) rises above the reference. With parking lots, guests arrive by car and are
/// turned away when the lots are full. With a campground, some come to camp a few nights (they park there and pay their nights).
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

        // Guests without a bike come on top, to rent one (only where the park has a rental station).
        if (RentalPrice(state) is { } rental)
        {
            long renters = (long)expectedPermille * state.FacilityRules.RenterSharePermille / 1000
                           * FacilityMath.BuyFactorPermille(rental.Product, rental.PriceCents) / 1000;
            int count = (int)(renters / 1000) + (ctx.Rng.ChancePermille((int)(renters % 1000)) ? 1 : 0);
            for (int i = 0; i < count; i++)
                TryAdmitGuest(ctx, renter: true);
        }
    }

    /// <summary>The first rental station's rental product and the park's price for it, or null without a rental station.</summary>
    private static (ProductType Product, long PriceCents)? RentalPrice(WorldState state)
    {
        foreach (var station in state.Facilities)
            if (FacilityMath.FindType(state, station.TypeId) is { Kind: FacilityKind.Rental } type && Bikes.RentalProduct(state, type, station) is { } product)
                return (product, FacilityMath.PriceOf(state, product));
        return null;
    }

    internal static int ExpectedArrivalsPermille(WorldState state, int minuteOfDay)
    {
        var rules = state.Rules;
        long reference = Math.Max(1, ReferenceArrivalCents(state));
        long feeFactor = 1000 + (reference - ArrivalCents(state)) * 500 / reference;
        feeFactor = Math.Clamp(feeFactor, 100, 1500);
        long weather = state.WeatherRules.ArrivalPermille(state.Weather.Today.Kind);
        long reputation = ReputationMath.DemandPermille(state);
        long marketing = Staff.StaffMath.MarketingPermille(state);
        return (int)(rules.BaseArrivalPermille * feeFactor / 1000 * ProfilePermille(rules.ArrivalProfile, minuteOfDay) / 1000 * weather / 1000
                     * reputation / 1000 * marketing / 1000);
    }

    /// <summary>What a day guest pays to get in: the entry fee and their share of the parking fee.</summary>
    internal static long ArrivalCents(WorldState state) =>
        state.Park.EntryFeeCents + LiftMath.ParkingShareCents(state, state.Park.ParkingFeeCents);

    /// <summary>What guests find fair to pay to get in (reference entry fee and parking share).</summary>
    internal static long ReferenceArrivalCents(WorldState state) =>
        state.Rules.ReferenceEntryFeeCents + LiftMath.ParkingShareCents(state, state.Rules.ReferenceParkingFeeCents);

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
    internal static int PlanLunch(SimContext ctx, int arrivalMinute)
    {
        var rules = ctx.State.Rules;
        if (!rules.HasLunch || arrivalMinute >= rules.LunchEndMinute) return -1;
        int from = Math.Max(rules.LunchStartMinute, arrivalMinute + 1);
        int span = rules.LunchEndMinute - from;
        if (span <= 0) return -1;
        return from + (ctx.Rng.NextInt(span) + ctx.Rng.NextInt(span)) / 2;
    }

    private static void TryAdmitGuest(SimContext ctx, bool renter = false)
    {
        var state = ctx.State;
        long fee = state.Park.EntryFeeCents;

        // Renters need a free bike (and the rental station's price).
        (Facility Station, FacilityType Type, RentalBike Bike)? bike = null;
        ProductType? rental = null;
        if (renter)
        {
            bike = FacilityMath.FreeRentalBike(state);
            rental = bike is { } b ? Bikes.RentalProduct(state, b.Type, b.Station) : null;
            if (rental is null)
            {
                state.Stats.RentersWithoutBike++;
                if (state.Facilities.FirstOrDefault(f => FacilityMath.FindType(state, f.TypeId)?.Kind == FacilityKind.Rental) is { } station)
                {
                    station.TurnedAwayToday++;
                    station.TurnedAway++;
                }
                state.Stats.TotalTurnedAway++;
                state.Stats.TurnedAwayToday++;
                ctx.Publish(new GuestTurnedAway(ctx.Tick, TurnAwayReason.NoRentalBike));
                return;
            }
        }
        long rentalCents = rental is null ? 0 : FacilityMath.PriceOf(state, rental);
        long cash = ctx.Rng.Range(2_000, 8_001) + rentalCents;

        // Campers: a free pitch, the camping chance at the night's price; they bring money for their nights and park at the camp.
        var camp = renter ? null : FacilityMath.FreeCampground(state);
        int nights = 0;
        if (camp is { } c && ctx.Rng.ChancePermille((int)((long)state.FacilityRules.CampChancePermille
                * FacilityMath.BuyFactorPermille(c.Night, FacilityMath.PriceOf(state, c.Night)) / 1000)))
        {
            nights = ctx.Rng.Range(state.FacilityRules.MinNights, state.FacilityRules.MaxNights + 1);
            cash += nights * (long)ctx.Rng.Range(3_000, 6_001);
        }
        long parking = nights > 0 ? 0 : LiftMath.ParkingShareCents(state, state.Park.ParkingFeeCents);
        long nightsCents = nights > 0 ? nights * FacilityMath.PriceOf(state, camp!.Value.Night) : 0;

        if (nights == 0 && LiftMath.ParkingCapacity(state) is { } capacity && LiftMath.DayGuests(state) >= capacity)
        {
            state.Stats.TotalTurnedAway++;
            state.Stats.TurnedAwayToday++;
            state.Stats.TotalTurnedAwayParkingFull++;
            ctx.Publish(new GuestTurnedAway(ctx.Tick, TurnAwayReason.ParkingFull));
            return;
        }

        // Too few people in the ticket office: some arrivals turn back at the queue.
        if (Staff.StaffMath.TicketTurnAwayPermille(state) is > 0 and var queue && ctx.Rng.ChancePermille(queue))
        {
            state.Stats.TotalTurnedAway++;
            state.Stats.TurnedAwayToday++;
            state.Stats.TotalTurnedAwayTicketQueue++;
            ctx.Publish(new GuestTurnedAway(ctx.Tick, TurnAwayReason.TicketQueue));
            return;
        }

        if (cash < fee + parking + nightsCents + rentalCents)
        {
            state.Stats.TotalTurnedAway++;
            state.Stats.TurnedAwayToday++;
            ctx.Publish(new GuestTurnedAway(ctx.Tick));
            return;
        }

        long reference = Math.Max(1, ReferenceArrivalCents(state));
        int feePenalty = (int)Math.Max(0, (fee + parking - reference) * 200 / reference) + Staff.StaffMath.TicketQueueMood(state);

        int lunchMinute = PlanLunch(ctx, GameTime.MinuteOfDay(ctx.Tick));
        var guest = new Guest
        {
            LunchMinute = lunchMinute,
            Id = state.AllocateEntityId(),
            CashCents = cash - fee - parking,
            BroughtCents = cash,
            PaidEntryCents = fee + parking,
            Happiness = Math.Clamp(ctx.Rng.Range(550, 801) - feePenalty, 0, 1000),
            ArrivedTick = ctx.Tick,
            PlannedStayMinutes = ctx.Rng.Range(60, 361),
            // Skill is triangular around 500: most riders are intermediate.
            Skill = (ctx.Rng.Range(0, 1001) + ctx.Rng.Range(0, 1001)) / 2,
            Style = (RiderStyle)ctx.Rng.NextInt(3),
            Energy = ctx.Rng.Range(650, 1001),
            Activity = RiderActivity.Wandering,
        };

        if (renter && !FacilityMath.EBikes(bike!.Value.Type, bike.Value.Station))
        {
            // Without a bike of their own: mostly beginners and casual riders (on e-bikes: anyone).
            int max = state.FacilityRules.RenterSkillMax;
            guest.Skill = (ctx.Rng.Range(0, max + 1) + ctx.Rng.Range(0, max + 1)) / 2;
            if (ctx.Rng.ChancePermille(667)) guest.Style = RiderStyle.Casual;
        }

        if (!renter && state.Reputation.InfluencerDue)
        {
            // The influencer rides like a good rider and stays the day.
            state.Reputation.InfluencerDue = false;
            guest.IsInfluencer = true;
            guest.Skill = state.ReputationRules.InfluencerSkill;
            guest.PlannedStayMinutes = state.ReputationRules.InfluencerStayMinutes;
        }

        state.Finance.Earn(MoneyCategory.EntryFees, fee);
        if (parking > 0)
        {
            state.Finance.Earn(MoneyCategory.ParkingFees, parking);
            state.Finance.TotalParkingCents += parking;
            state.Finance.ParkingTodayCents += parking;
            // A car maker sponsor pays on top of every parking fee.
            long bonus = parking * Sponsors.SponsorMath.Benefit(state, Sponsors.SponsorBenefitKind.ParkingRevenue) / 1000;
            if (bonus > 0)
            {
                state.Finance.Earn(MoneyCategory.Sponsors, bonus);
                state.Finance.TotalSponsorCents += bonus;
                state.Finance.SponsorTodayCents += bonus;
            }
        }
        if (nights > 0)
        {
            var (campground, _, night) = camp!.Value;
            for (int n = 0; n < nights; n++)
                FacilityMath.Sell(state, ctx.Tick, campground, guest, night);
            guest.CampgroundId = campground.Id;
            guest.NightsLeft = nights;
            // Campers ride the whole day.
            guest.PlannedStayMinutes = Math.Max(guest.PlannedStayMinutes, state.Rules.CloseMinute - GameTime.MinuteOfDay(ctx.Tick));
            state.Stats.TotalCampers++;
        }
        if (renter)
            Bikes.Rent(ctx, guest, bike!.Value.Station, bike.Value.Type, bike.Value.Bike, rental!);
        // The park sponsor's brand: some guests like it, some find it too commercial.
        if (Sponsors.SponsorMath.DealOn(state, 0) is { Pays: true } deal && Sponsors.SponsorMath.Find(state, deal.SponsorId) is { Mood: not 0 } brand)
            guest.Happiness = Math.Clamp(guest.Happiness + brand.Mood, 0, 1000);
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
        bool crowded = ParkSchedule.AwakeGuests(state) > state.Rules.Capacity;
        int dirt = Staff.StaffMath.CleaningMoodLoss(state, ctx.Tick); // too few cleaners
        bool raining = Weather.WeatherMath.IsRaining(state, ctx.Tick);

        // Iterate in list order and compact in place: deterministic and allocation-free.
        int write = 0;
        for (int read = 0; read < guests.Count; read++)
        {
            var guest = guests[read];
            guest.Alcohol = Math.Max(0, guest.Alcohol - state.FacilityRules.AlcoholDecayPerMinute);
            // A seriously injured rider stays where they fell (even after closing) until the helicopter has flown them out.
            GuestLeaveReason? leave = guest.Activity == RiderActivity.Camping
                ? Camping.Night(ctx, guest)
                : guest.Activity == RiderActivity.Injured
                    ? ctx.Tick >= guest.RescueAtTick ? GuestLeaveReason.Evacuated : null
                    : guest.Activity == RiderActivity.Apres
                        ? Catering.ApresMinute(ctx, guest) ? GuestLeaveReason.StayCompleted : null
                        : closed || lastRides && IsBetweenLaps(guest)
                            ? GuestLeaveReason.ParkClosed
                            : UpdateGuest(ctx, guest, crowded, raining, dirt);
            bool doneForTheDay = leave is GuestLeaveReason.ParkClosed or GuestLeaveReason.StayCompleted or GuestLeaveReason.Tired
                or GuestLeaveReason.Drunk;
            // Done for the day: maybe a drink at the bar first (après-bike), then campers go to their tent.
            if (doneForTheDay && guest.Activity == RiderActivity.Idle && leave != GuestLeaveReason.Drunk && Catering.TryApres(ctx, ctx.Network, guest))
                leave = null;
            else if (doneForTheDay && Camping.TryGoToSleep(ctx, guest))
                leave = null;
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
                    Catering.BuyMerch(ctx, ctx.Network, guest);
                Bikes.Return(ctx, guest);
                ctx.Publish(new GuestLeft(ctx.Tick, guest.Id, reason));
                ReputationSystem.Review(ctx, guest, reason);
            }
            else
                guests[write++] = guest;
            if (leave is not null && guest.Activity == RiderActivity.Queuing)
                LiftSystem.LeaveQueue(state, guest);
        }
        guests.RemoveRange(write, guests.Count - write);
    }

    /// <summary>
    /// Between laps a guest now and then buys something: at the nearest bar in reach (scenarios with bars, see
    /// <see cref="Catering.TryImpulse"/>), or a snack anywhere at the park's snack price (scenarios without).
    /// </summary>
    private static void TrySnack(SimContext ctx, Guest guest)
    {
        var state = ctx.State;
        if (!FacilityMath.BarsNeeded(state))
        {
            if (guest.CashCents < state.Rules.SnackPriceCents || !ctx.Rng.ChancePermille(SnackChancePermille)) return;
            guest.CashCents -= state.Rules.SnackPriceCents;
            state.Finance.Earn(MoneyCategory.Food, state.Rules.SnackPriceCents);
            guest.Happiness += SnackHappinessBoost;
            return;
        }
        var point = FacilityMath.GuestPoint(state, ctx.Network, guest);
        if (!Catering.TryImpulse(ctx, guest, point, 1000))
            Catering.TryImpulse(ctx, guest, point, 1000, FacilityKind.Shop); // protectors at the pro shop
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

    private static GuestLeaveReason? UpdateGuest(SimContext ctx, Guest guest, bool crowded, bool raining, int dirt)
    {
        var rules = ctx.State.Rules;
        var liftRules = ctx.State.LiftRules;
        bool onTheWay = guest.Activity is RiderActivity.Climbing or RiderActivity.Descending or RiderActivity.Walking or RiderActivity.OnLift;

        guest.Happiness += ctx.Rng.Range(-1, 2);
        if (crowded)
            guest.Happiness -= CrowdingPenalty;
        guest.Happiness -= dirt;

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
        // Down with a broken bike: to the service shop or the rental station; if nobody can fix it, home.
        if (guest.Activity == RiderActivity.Idle && guest.BikeBroken && !Bikes.TryFix(ctx, guest))
            return GuestLeaveReason.BikeBroken;
        if (guest.Activity == RiderActivity.Eating)
            return null; // at the workbench
        if (guest.Activity == RiderActivity.Idle && guest.Alcohol >= ctx.State.FacilityRules.TooDrunkAlcohol)
            return GuestLeaveReason.Drunk;
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
        state.Finance.Spend(MoneyCategory.ParkStaff, Staff.StaffMath.DailyCostCents(state));
        long wages = state.Crew.Sum(m => m.WagePerDayCents);
        if (wages > 0)
        {
            state.Finance.Spend(MoneyCategory.Wages, wages);
            state.Finance.TotalWagesCents += wages;
            state.Finance.WagesTodayCents += wages;
        }
        // Photo spots: each camera's upkeep.
        long cameras = state.Ways.Sum(w => w.Features.Count(f => f.Camera)) * state.SponsorRules.CameraUpkeepPerDayCents;
        if (cameras > 0) state.Finance.Spend(MoneyCategory.CameraUpkeep, cameras);
        long facilityUpkeep = 0;
        foreach (var facility in state.Facilities)
        {
            if (FacilityMath.FindType(state, facility.TypeId) is not { } type) continue;
            long upkeep = FacilityMath.Upkeep(type, facility);
            if (upkeep <= 0) continue;
            state.Finance.Spend(type.Kind switch
            {
                FacilityKind.Bar => MoneyCategory.BarUpkeep,
                FacilityKind.Campground => MoneyCategory.CampUpkeep,
            FacilityKind.Rental or FacilityKind.Service => MoneyCategory.BikeCosts,
                _ => MoneyCategory.ShopUpkeep,
            }, upkeep);
            facilityUpkeep += upkeep;
        }
        state.Finance.TotalFacilityUpkeepCents += facilityUpkeep;
        foreach (var lift in state.Lifts)
        {
            if (lift.OperatorId is null && lift.InService && LiftNetwork.FindType(state, lift.TypeId) is { UpkeepPerDayCents: > 0 } own)
            {
                state.Finance.Spend(MoneyCategory.LiftUpkeep, own.UpkeepPerDayCents);
                state.Finance.TotalLiftUpkeepCents += own.UpkeepPerDayCents;
                state.Finance.LiftUpkeepTodayCents += own.UpkeepPerDayCents;
            }
            if (lift.BikeAccess is not { } access || LiftNetwork.FindOperator(state, lift.OperatorId) is not { } op) continue;
            if (access.TierIndex < 0 || access.TierIndex >= op.BikeAccessTiers.Count) continue;
            long fee = op.BikeAccessTiers[access.TierIndex].DailyFeeCents;
            state.Finance.Spend(MoneyCategory.LiftFees, fee);
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
            LiftUpkeepCents: state.Finance.LiftUpkeepTodayCents,
            LiftTicketsCents: state.Finance.LiftTicketsTodayCents,
            LoanPaymentsCents: state.Finance.LoanPaymentsTodayCents,
            DebtCents: Banking.LoanMath.DebtCents(state),
            DaysInDebt: CountDebtDays(ctx),
            FoodCents: state.Finance.FoodTodayCents,
            MerchCents: state.Finance.MerchTodayCents,
            FacilityUpkeepCents: facilityUpkeep,
            DrinksCents: state.Finance.DrinksTodayCents,
            ParkingCents: state.Finance.ParkingTodayCents,
            CampingCents: state.Finance.CampingTodayCents,
            RentalCents: state.Finance.RentalTodayCents,
            RepairCents: state.Finance.RepairTodayCents,
            SponsorCents: state.Finance.SponsorTodayCents,
            ClipCents: state.Finance.ClipTodayCents,
            ByCategory: new MoneyByCategory(state.Finance.TodayCents));
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
            lift.Stats.PassesSoldToday = 0;
            lift.Stats.PassRevenueTodayCents = 0;
            lift.Stats.NoPassToday = 0;
        }
        state.Finance.LiftFeesTodayCents = 0;
        state.Finance.LiftTicketsTodayCents = 0;
        state.Finance.LoanPaymentsTodayCents = 0;
        state.Finance.FoodTodayCents = 0;
        state.Finance.MerchTodayCents = 0;
        state.Finance.DrinksTodayCents = 0;
        state.Finance.ParkingTodayCents = 0;
        state.Finance.CampingTodayCents = 0;
        state.Finance.RentalTodayCents = 0;
        state.Finance.RepairTodayCents = 0;
        state.Finance.SponsorTodayCents = 0;
        state.Finance.ClipTodayCents = 0;
        foreach (var sales in state.Finance.ProductSales)
        {
            sales.SoldToday = 0;
            sales.RevenueTodayCents = 0;
        }
        state.Finance.TodayCents.Clear();
        foreach (var facility in state.Facilities)
        {
            facility.RevenueTodayCents = 0;
            facility.CustomersToday = 0;
            facility.TurnedAwayToday = 0;
            facility.OverpricedToday = 0;
            foreach (var sales in facility.Sales)
            {
                sales.SoldToday = 0;
                sales.RevenueTodayCents = 0;
            }
        }
        state.Finance.WagesTodayCents = 0;
        state.Stats.VisitorsToday = 0;
        state.Reputation.ReviewsToday = 0;
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
