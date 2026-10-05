using Bikepark.Sim.Core;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Reporting;

/// <summary>
/// Headline numbers derived from a <see cref="WorldState"/>. Read-only; used by SimRunner and the HUD.
/// Happiness values are 0..1000. <see cref="AverageHappiness"/> covers guests currently in the park,
/// <see cref="AverageExitHappiness"/> all guests who have left so far.
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
    string? StateHash)
{
    public static KpiReport From(WorldState state, bool includeHash = true)
    {
        int average = state.Guests.Count == 0 ? 0 : (int)(state.Guests.Sum(g => (long)g.Happiness) / state.Guests.Count);
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
            StateHash: includeHash ? Persistence.StateHash.Compute(state) : null);
    }
}
