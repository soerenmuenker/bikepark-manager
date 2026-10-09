using Bikepark.Sim.Core;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Reporting;

/// <summary>
/// Headline numbers derived from a <see cref="WorldState"/>. Read-only; used by SimRunner and the HUD.
/// Happiness values are 0..1000. <see cref="AverageHappiness"/> covers guests currently in the park,
/// <see cref="AverageExitHappiness"/> all guests who have left so far. Run fun is 0..1000. Lift waits are minutes
/// per boarding; <see cref="MaxQueue"/> is the longest queue seen at any lift.
/// </summary>
public sealed record KpiReport(
    string ScenarioId,
    ulong Seed,
    long Tick,
    long DaysElapsed,
    string ParkName,
    long EntryFeeCents,
    long MoneyCents,
    long TotalRevenueCents,
    long TotalExpensesCents,
    long TotalVisitors,
    long TotalTurnedAway,
    int GuestsInPark,
    int AverageHappiness,
    int AverageExitHappiness,
    long TotalLeftUnhappy,
    int AccessPaths,
    int Trails,
    long RunsCompleted,
    int AverageRunFun,
    int RidersOnTrails,
    long TotalTurnedAwayParkingFull,
    int ParkingOccupancyPermille,
    long LiftRides,
    int AverageWaitMinutes,
    int MaxQueue,
    int GuestsQueuing,
    int RidersOnLifts,
    long TotalLiftFeesCents,
    int GuestsEating,
    long TotalFoodCents,
    int TrailsClosed,
    int RainDays,
    int WetnessPermille,
    int? RatingTenths,
    long TotalReviews,
    int DemandPermille,
    int Xp,
    int Level,
    long Crashes,
    long SeriousCrashes,
    int CrashesPer1000Runs,
    int GuestsInjured,
    long TotalInsuranceCents,
    string? StateHash)
{
    /// <param name="network">The way network, for the park XP (without it: XP from visitors only).</param>
    public static KpiReport From(WorldState state, bool includeHash = true, WayNetwork? network = null)
    {
        int xp = Reputation.ParkProgress.Xp(state, network ?? WayNetwork.Empty).Total;
        int average = state.Guests.Count == 0 ? 0 : (int)(state.Guests.Sum(g => (long)g.Happiness) / state.Guests.Count);
        long runs = state.Ways.Sum(w => w.Stats.Runs);
        long liftRides = state.Lifts.Sum(l => l.Stats.Riders);
        int? parking = Lifts.LiftMath.ParkingCapacity(state);
        return new KpiReport(
            ScenarioId: state.ScenarioId,
            Seed: state.Seed,
            Tick: state.Tick,
            DaysElapsed: GameTime.Day(state.Tick),
            ParkName: state.Park.Name,
            EntryFeeCents: state.Park.EntryFeeCents,
            MoneyCents: state.Finance.MoneyCents,
            TotalRevenueCents: state.Finance.TotalRevenueCents,
            TotalExpensesCents: state.Finance.TotalExpensesCents,
            TotalVisitors: state.Stats.TotalVisitors,
            TotalTurnedAway: state.Stats.TotalTurnedAway,
            GuestsInPark: state.Guests.Count,
            AverageHappiness: average,
            AverageExitHappiness: state.Stats.TotalGuestsLeft == 0 ? 0 : (int)(state.Stats.SumExitHappiness / state.Stats.TotalGuestsLeft),
            TotalLeftUnhappy: state.Stats.TotalLeftUnhappy,
            AccessPaths: state.Ways.Count(w => w.Kind == WayKind.AccessPath),
            Trails: state.Ways.Count(w => w.Kind == WayKind.Trail),
            RunsCompleted: runs,
            AverageRunFun: runs == 0 ? 0 : (int)(state.Ways.Sum(w => w.Stats.SumFun) / runs),
            RidersOnTrails: state.Guests.Count(g => g.Activity == RiderActivity.Descending),
            TotalTurnedAwayParkingFull: state.Stats.TotalTurnedAwayParkingFull,
            ParkingOccupancyPermille: parking is > 0 ? (int)Math.Min(1000, state.Guests.Count * 1000L / parking.Value) : 0,
            LiftRides: liftRides,
            AverageWaitMinutes: liftRides == 0 ? 0 : (int)(state.Lifts.Sum(l => l.Stats.SumWaitMinutes) / liftRides),
            MaxQueue: state.Lifts.Count == 0 ? 0 : state.Lifts.Max(l => l.Stats.MaxQueue),
            GuestsQueuing: state.Guests.Count(g => g.Activity == RiderActivity.Queuing),
            RidersOnLifts: state.Guests.Count(g => g.Activity == RiderActivity.OnLift),
            TotalLiftFeesCents: state.Finance.TotalLiftFeesCents,
            GuestsEating: state.Guests.Count(g => g.Activity == RiderActivity.Eating),
            TotalFoodCents: state.Finance.TotalFoodCents,
            TrailsClosed: state.Ways.Count(w => w.Kind == WayKind.Trail && w.Built && !w.IsRideable),
            RainDays: state.Weather.RainDays,
            WetnessPermille: state.Weather.WetnessPermille,
            RatingTenths: Reputation.ReputationMath.RatingTenths(state),
            TotalReviews: state.Reputation.TotalReviews,
            DemandPermille: Reputation.ReputationMath.DemandPermille(state),
            Xp: xp,
            Level: Reputation.ParkProgress.Level(state.ReputationRules, xp),
            Crashes: state.Safety.TotalCrashes,
            SeriousCrashes: state.Safety.TotalSerious,
            CrashesPer1000Runs: runs == 0 ? 0 : (int)(state.Safety.TotalCrashes * 1000 / runs),
            GuestsInjured: state.Guests.Count(g => g.Activity == RiderActivity.Injured),
            TotalInsuranceCents: state.Safety.TotalInsuranceCents,
            StateHash: includeHash ? Persistence.StateHash.Compute(state) : null);
    }
}
