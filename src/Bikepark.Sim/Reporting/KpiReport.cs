using Bikepark.Sim.Core;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Reporting;

/// <summary>
/// Headline numbers derived from a <see cref="WorldState"/>. Read-only; used by SimRunner and the HUD.
/// Happiness values are 0..1000. <see cref="AverageHappiness"/> covers guests currently in the park,
/// <see cref="AverageExitHappiness"/> all guests who have left so far. Run fun is 0..1000.
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
    string? StateHash)
{
    public static KpiReport From(WorldState state, bool includeHash = true)
    {
        int average = state.Guests.Count == 0 ? 0 : (int)(state.Guests.Sum(g => (long)g.Happiness) / state.Guests.Count);
        long runs = state.Ways.Sum(w => w.Stats.Runs);
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
            StateHash: includeHash ? Persistence.StateHash.Compute(state) : null);
    }
}
