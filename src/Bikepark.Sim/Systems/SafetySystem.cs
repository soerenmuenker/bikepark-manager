using Bikepark.Sim.Core;
using Bikepark.Sim.Safety;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Closes each day's accident book: records the day's minor and serious crashes and each trail's runs and crashes, kept
/// for <see cref="CrashRules.RateDays"/> days (the trails' crash rates, see <see cref="CrashMath.TrailRatePer10k"/>). Crashes
/// cost no money. Crashes themselves happen in <see cref="RiderSystem"/>; seriously injured riders are flown out in
/// <see cref="GuestSystem"/>. Nothing happens without <see cref="CrashRules.Enabled"/>.
/// </summary>
internal sealed class SafetySystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        var rules = state.CrashRules;
        if (!rules.Enabled || !GameTime.IsLastMinuteOfDay(ctx.Tick)) return;

        var safety = state.Safety;
        safety.History.Add(new AccidentDay(GameTime.Day(ctx.Tick), safety.MinorToday, safety.SeriousToday));
        if (safety.History.Count > rules.RateDays)
            safety.History.RemoveRange(0, safety.History.Count - rules.RateDays);
        safety.MinorToday = 0;
        safety.SeriousToday = 0;

        // Before FinanceSystem resets RunsToday.
        foreach (var trail in state.Ways)
        {
            if (trail.Kind != Trails.WayKind.Trail || !trail.Built) continue;
            var stats = trail.Stats;
            stats.History.Add(new Trails.TrailDay(stats.RunsToday, stats.CrashesToday));
            if (stats.History.Count > rules.RateDays)
                stats.History.RemoveRange(0, stats.History.Count - rules.RateDays);
            stats.CrashesToday = 0;
        }
    }
}
