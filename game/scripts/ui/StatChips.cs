using Bikepark.Sim.Core;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Reputation;
using Godot;

namespace Bikepark.Game.Ui;

/// <summary>A headline stat in the bar: not clickable; its hover tip is built when shown, so it is always current.</summary>
public partial class StatChip : PanelContainer
{
    private readonly Func<string>? _tip;

    public StatChip() { }

    public StatChip(Func<string> tip)
    {
        _tip = tip;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override GodotObject? _MakeCustomTooltip(string forText) => UiTheme.Tooltip(_tip?.Invoke() ?? forText);
}

/// <summary>The breakdowns behind the bar's headline stats (hover tips).</summary>
internal static class StatTips
{
    public static string Money(HudContext ctx)
    {
        var state = ctx.Sim.State;
        var f = state.Finance;
        string text = $"Money: {UiTheme.Money(f.MoneyCents)}\n" +
                      $"Today so far: {Signed(f.RevenueTodayCents - f.ExpensesTodayCents)}\n\n" +
                      $"• Income {UiTheme.Money(f.RevenueTodayCents)}\n" +
                      $"• Expenses {UiTheme.Money(f.ExpensesTodayCents)}";
        if (state.Park.Bankrupt) text += "\n• Bankrupt: the park is closed for good";
        else if (f.DaysInDebt > 0)
            text += state.Rules.BankruptcyDays > 0
                ? $"\n• In the red for {f.DaysInDebt} of {state.Rules.BankruptcyDays} days before bankruptcy"
                : $"\n• In the red for {f.DaysInDebt} days";
        return text + "\n\nBudget, loans and sponsors: Finances [M]";
    }

    public static string Guests(HudContext ctx)
    {
        var k = ctx.Kpi;
        var state = ctx.Sim.State;
        return $"Guests: {k.GuestsInPark} in the park\n" +
               $"{state.Stats.VisitorsToday} came today, {state.Stats.TurnedAwayToday} turned away\n\n" +
               $"• On trails {k.RidersOnTrails}\n" +
               $"• In lift queues {k.GuestsQueuing}, on lifts {k.RidersOnLifts}\n" +
               $"• Eating {k.GuestsEating}" +
               (k.CampersAsleep > 0 ? $"\n• Campers asleep {k.CampersAsleep}" : "") +
               (k.GuestsInjured > 0 ? $"\n• Injured {k.GuestsInjured}" : "");
    }

    public static string Mood(HudContext ctx)
    {
        var guests = ctx.Sim.State.Guests.Where(g => g.Activity != Bikepark.Sim.State.RiderActivity.Camping).ToList();
        if (guests.Count == 0)
            return ctx.Kpi.AverageExitHappiness > 0
                ? $"Mood\nNobody in the park. Guests left with {ctx.Kpi.AverageExitHappiness / 10} % on average."
                : "Mood\nNobody in the park yet.";
        int happy = guests.Count(g => g.Happiness >= 650), unhappy = guests.Count(g => g.Happiness < 450);
        return $"Mood: {ctx.Kpi.AverageHappiness / 10} % on average\n\n" +
               $"• Happy {happy * 100 / guests.Count} %\n" +
               $"• So-so {(guests.Count - happy - unhappy) * 100 / guests.Count} %\n" +
               $"• Unhappy {unhappy * 100 / guests.Count} %\n\n" +
               "Fun runs, short queues and food lift it; crashes, waiting and high prices lower it.";
    }

    public static string Rating(HudContext ctx)
    {
        var state = ctx.Sim.State;
        var k = ctx.Kpi;
        string text = k.RatingTenths is { } r
            ? $"Rating: {r / 10.0:0.0} stars from {k.TotalReviews} reviews\n"
            : $"No rating yet ({k.TotalReviews} reviews so far)\n";
        text += $"Park level {k.Level}\n";
        var lines = new List<string>();
        foreach (var (group, name) in new[] { (SkillGroup.Beginner, "Beginners"), (SkillGroup.Intermediate, "Intermediates"), (SkillGroup.Expert, "Experts") })
            if (ReputationMath.RatingTenths(state, group) is { } g) lines.Add($"{name} {g / 10.0:0.0} ★");
        var reviews = state.Reputation.Reviews;
        if (reviews.Count > 0)
        {
            var aspects = new (string Name, double Average)[]
            {
                ("fun", reviews.Average(x => x.Fun)), ("safety", reviews.Average(x => x.Safety)),
                ("smoothness", reviews.Average(x => x.Smoothness)), ("challenge", reviews.Average(x => x.Challenge)),
                ("jumps", reviews.Average(x => x.Jumps)), ("variety", reviews.Average(x => x.Variety)),
                ("value", reviews.Average(x => x.Value)),
            };
            var weakest = aspects.MinBy(a => a.Average);
            lines.Add($"Weakest: {weakest.Name} ({weakest.Average / 200:0.0} ★)");
        }
        return text + (lines.Count > 0 ? "\n" + string.Join("\n", lines.Select(l => "• " + l)) : "") + "\n\nReviews and sub-ratings: Rating [U]";
    }

    public static string Queue(HudContext ctx)
    {
        var state = ctx.Sim.State;
        var lines = new List<string>();
        foreach (var lift in state.Lifts.Where(l => l.InService))
        {
            if (LiftNetwork.FindType(state, lift.TypeId) is not { } type) continue;
            int wait = LiftMath.ExpectedWaitMinutes(type, lift.BikeCarrierPermille, lift.Queue.Count);
            lines.Add($"{lift.Name}: {lift.Queue.Count} queuing" + (lift.IsStopped(state.Tick) ? ", stopped" : wait < 0 ? ", no bikes" : wait > 0 ? $", {wait} min wait" : ""));
        }
        return "Lift queues\nRiders waiting and the expected wait.\n\n" +
               (lines.Count == 0 ? "• No lift running" : string.Join("\n", lines.Select(l => "• " + l)));
    }

    public static string Crew(HudContext ctx)
    {
        var state = ctx.Sim.State;
        int working = state.Crew.Count(c => c.JobId != 0);
        string staff = "";
        if (state.StaffRules.Enabled)
        {
            staff = $"• Park staff {Bikepark.Sim.Staff.StaffMath.Headcount(state)}: {UiTheme.Money(Bikepark.Sim.Staff.StaffMath.WagesPerDay(state))} a day\n";
            var short_ = state.StaffRoles.Where(r => r.Effect != Bikepark.Sim.Staff.StaffEffect.Marketing)
                .Select(r => (Role: r, Coverage: Bikepark.Sim.Staff.StaffMath.CoveragePermille(state, r.Effect)))
                .Where(x => x.Coverage < 1000).OrderBy(x => x.Coverage).FirstOrDefault();
            if (short_.Role is { } role) staff += $"• Short of staff: {role.Name.ToLowerInvariant()} ({short_.Coverage / 10} % covered)\n";
        }
        return $"Crew: {state.Crew.Count} workers · {UiTheme.Money(state.Crew.Sum(m => m.WagePerDayCents))} a day\n\n" +
               $"• Working {working}, idle {state.Crew.Count - working}\n" +
               $"• Jobs waiting {state.Jobs.Count}\n" + staff + "\n" +
               "They work during opening hours only. Hire, staff and plan: Crew [C]";
    }

    public static string Wood(HudContext ctx)
    {
        var state = ctx.Sim.State;
        int needed = state.Jobs.Where(j => !j.WoodTaken).Sum(j => j.Wood);
        return $"Wood: {state.WoodStock} in stock\n" +
               (needed > 0 ? $"Planned jobs need {needed}." : "No planned job needs wood.") +
               "\n\nFell trees or buy wood in the Crew menu.";
    }

    private static string Signed(long cents) => cents >= 0 ? "+" + UiTheme.Money(cents) : "−" + UiTheme.Money(-cents);
}
