using Bikepark.Sim.Core;
using Bikepark.Sim.Safety;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Closes each day's accident book: records the day's minor and serious crashes and charges the insurance premium, a
/// base plus a charge per accident in the last <see cref="CrashRules.InsuranceDays"/> days. Crashes themselves happen in
/// <see cref="RiderSystem"/>; seriously injured riders are flown out in <see cref="GuestSystem"/>. Nothing happens
/// without <see cref="CrashRules.Enabled"/>.
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
        if (safety.History.Count > rules.InsuranceDays)
            safety.History.RemoveRange(0, safety.History.Count - rules.InsuranceDays);
        safety.MinorToday = 0;
        safety.SeriousToday = 0;

        long premium = CrashMath.Premium(rules, safety);
        state.Finance.Spend(premium);
        safety.TotalInsuranceCents += premium;
        safety.InsuranceTodayCents = premium;
    }
}
