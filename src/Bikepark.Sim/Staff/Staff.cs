using Bikepark.Sim.Core;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Staff;

/// <summary>What a park staff role does (each role's shortage costs something different).</summary>
public enum StaffEffect : byte
{
    /// <summary>Park managers supervise everyone else (staff and crew): too few, and all of them work worse.</summary>
    Supervision = 0,

    /// <summary>The ticket office: too few, and arrivals queue (worse mood) or turn back.</summary>
    Tickets = 1,

    /// <summary>First aid: too few, and the injured wait longer for the helicopter; minor crashes hurt more.</summary>
    FirstAid = 2,

    /// <summary>Cleaning: too few, and guests' mood sinks slowly all day.</summary>
    Cleaning = 3,

    /// <summary>Marketing: no shortage; each marketer brings more guests (less with every extra one).</summary>
    Marketing = 4,
}

/// <summary>A park staff role (content, <c>data/staff_roles.json</c>): the park employs a number of each.</summary>
public sealed class StaffRole
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public StaffEffect Effect { get; set; }
    public long WagePerDayCents { get; set; }

    /// <summary>
    /// What one of them covers: managers – people supervised; ticket office – visitors a day; first aid – trails and lifts;
    /// cleaning – visitors a day (each building counts as <see cref="StaffRules.FacilityCleaningVisitors"/>); marketing –
    /// demand in permille (the first marketer).
    /// </summary>
    public int CoversPer { get; set; } = 1;

    public int MaxCount { get; set; } = 10;

    /// <summary>Park level needed to employ any (a sandbox has everything).</summary>
    public int RequiredLevel { get; set; }

    public List<string> Validate()
    {
        var errors = new List<string>();
        string p = $"staffRoles[{Id}]";
        if (string.IsNullOrWhiteSpace(Id)) errors.Add("staffRoles: id is required");
        if (string.IsNullOrWhiteSpace(Name)) errors.Add($"{p}.name is required");
        if (!Enum.IsDefined(Effect)) errors.Add($"{p}.effect is unknown");
        if (WagePerDayCents < 0) errors.Add($"{p}.wagePerDayCents must be >= 0");
        if (CoversPer is < 1 or > 100_000) errors.Add($"{p}.coversPer must be within 1..100000");
        if (MaxCount is < 0 or > 100) errors.Add($"{p}.maxCount must be within 0..100");
        if (RequiredLevel is < 0 or > Reputation.ReputationRules.MaxLevel)
            errors.Add($"{p}.requiredLevel must be within 0..{Reputation.ReputationRules.MaxLevel}");
        return errors;
    }
}

/// <summary>How many of a role the park employs.</summary>
public sealed class StaffPosition
{
    public string RoleId { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>
/// Park staff tuning (scenario). Off by default: then the park pays the flat <see cref="ParkRules.DailyUpkeepCents"/>
/// for its base staff and nothing depends on staffing.
/// </summary>
public sealed class StaffRules
{
    public bool Enabled { get; set; }

    /// <summary>Staff employed when the scenario starts.</summary>
    public List<StaffPosition> Start { get; set; } = [];

    /// <summary>People (staff and crew) who need no manager: a small park runs without one.</summary>
    public int FreeHeadcount { get; set; } = 6;

    /// <summary>Without any manager, everyone else works at this share (permille).</summary>
    public int SupervisionFloorPermille { get; set; } = 700;

    /// <summary>Without any ticket office staff, this share of arrivals turns back (permille; less with partial staff).</summary>
    public int TicketTurnAwayPermille { get; set; } = 600;

    /// <summary>Mood lost on arrival when the ticket office has nobody (less with partial staff).</summary>
    public int TicketQueueMood { get; set; } = 150;

    /// <summary>Without first aid, minor crashes cost this much more mood (permille; the helicopter wait doubles).</summary>
    public int MinorCrashExtraMoodPermille { get; set; } = 500;

    /// <summary>Mood lost per minute in the park without any cleaning staff (less often with partial staff).</summary>
    public int CleaningMoodPerMinute { get; set; } = 1;

    /// <summary>Each bar, shop or campground adds this many visitors' worth of cleaning.</summary>
    public int FacilityCleaningVisitors { get; set; } = 20;

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (FreeHeadcount is < 0 or > 1000) errors.Add("staffRules.freeHeadcount must be within 0..1000");
        if (SupervisionFloorPermille is < 0 or > 1000) errors.Add("staffRules.supervisionFloorPermille must be within 0..1000");
        if (TicketTurnAwayPermille is < 0 or > 1000) errors.Add("staffRules.ticketTurnAwayPermille must be within 0..1000");
        if (TicketQueueMood is < 0 or > 1000) errors.Add("staffRules.ticketQueueMood must be within 0..1000");
        if (MinorCrashExtraMoodPermille is < 0 or > 5000) errors.Add("staffRules.minorCrashExtraMoodPermille must be within 0..5000");
        if (CleaningMoodPerMinute is < 0 or > 100) errors.Add("staffRules.cleaningMoodPerMinute must be within 0..100");
        if (FacilityCleaningVisitors is < 0 or > 10_000) errors.Add("staffRules.facilityCleaningVisitors must be within 0..10000");
        foreach (var position in Start)
            if (position.Count < 0) errors.Add($"staffRules.start[{position.RoleId}].count must be >= 0");
        return errors;
    }
}

/// <summary>
/// Park staff: what the park needs, how well it is covered and what that does. Pure functions of <see cref="WorldState"/>
/// (the need comes from saved things: visitors, trails, lifts, buildings, head count); with staff off, everything is
/// fully covered.
/// </summary>
public static class StaffMath
{
    public static StaffRole? FindRole(WorldState state, string roleId) => state.StaffRoles.FirstOrDefault(r => r.Id == roleId);

    public static int Count(WorldState state, string roleId) => state.Staff.FirstOrDefault(p => p.RoleId == roleId)?.Count ?? 0;

    /// <summary>All staff employed (all roles).</summary>
    public static int Headcount(WorldState state) => state.Staff.Sum(p => p.Count);

    /// <summary>What the staff of one effect covers together (count × <see cref="StaffRole.CoversPer"/>).</summary>
    public static long Covers(WorldState state, StaffEffect effect)
    {
        long covers = 0;
        foreach (var role in state.StaffRoles)
            if (role.Effect == effect) covers += (long)Count(state, role.Id) * role.CoversPer;
        return covers;
    }

    public static int CountOf(WorldState state, StaffEffect effect) =>
        state.StaffRoles.Where(r => r.Effect == effect).Sum(r => Count(state, r.Id));

    /// <summary>The day's visitors to plan for: yesterday's or today's so far, whichever is more.</summary>
    public static int VisitorsBasis(WorldState state) =>
        Math.Max(state.Stats.VisitorsToday, state.DayHistory.Count > 0 ? state.DayHistory[^1].Visitors : 0);

    /// <summary>What the park needs of an effect, in the unit of <see cref="StaffRole.CoversPer"/> (0 = nothing).</summary>
    public static long Need(WorldState state, StaffEffect effect) => effect switch
    {
        StaffEffect.Supervision => Math.Max(0, Headcount(state) - CountOf(state, StaffEffect.Supervision) + state.Crew.Count - state.StaffRules.FreeHeadcount),
        StaffEffect.Tickets => VisitorsBasis(state),
        StaffEffect.FirstAid => state.Ways.Count(w => w.Built && w.Kind == WayKind.Trail) + state.Lifts.Count(l => l.InService && !l.Closed),
        StaffEffect.Cleaning => VisitorsBasis(state) + (long)state.Facilities.Count * state.StaffRules.FacilityCleaningVisitors,
        _ => 0,
    };

    /// <summary>Covered share of the need, before supervision (1000 = enough).</summary>
    public static int RawCoveragePermille(WorldState state, StaffEffect effect)
    {
        long need = Need(state, effect);
        return need <= 0 ? 1000 : (int)Math.Min(1000, Covers(state, effect) * 1000 / need);
    }

    /// <summary>How well everyone else works under the managers there are (1000 = fully supervised).</summary>
    public static int SupervisionPermille(WorldState state) =>
        !state.StaffRules.Enabled ? 1000 : Math.Max(state.StaffRules.SupervisionFloorPermille, RawCoveragePermille(state, StaffEffect.Supervision));

    /// <summary>The crew's work speed share from supervision (1000 with staff off).</summary>
    public static int CrewSupervisionPermille(WorldState state) => SupervisionPermille(state);

    /// <summary>Covered share of the need, with supervision (1000 = enough, and always with staff off).</summary>
    public static int CoveragePermille(WorldState state, StaffEffect effect)
    {
        if (!state.StaffRules.Enabled) return 1000;
        return effect switch
        {
            StaffEffect.Supervision => RawCoveragePermille(state, effect),
            StaffEffect.Marketing => 1000,
            _ => RawCoveragePermille(state, effect) * SupervisionPermille(state) / 1000,
        };
    }

    /// <summary>What is missing (0 = enough, 1000 = nobody).</summary>
    public static int ShortagePermille(WorldState state, StaffEffect effect) => 1000 - CoveragePermille(state, effect);

    /// <summary>The share of arrivals that turns back at the ticket office queue (permille).</summary>
    public static int TicketTurnAwayPermille(WorldState state) =>
        ShortagePermille(state, StaffEffect.Tickets) * state.StaffRules.TicketTurnAwayPermille / 1000;

    /// <summary>Mood lost on arrival in the ticket office queue.</summary>
    public static int TicketQueueMood(WorldState state) =>
        ShortagePermille(state, StaffEffect.Tickets) * state.StaffRules.TicketQueueMood / 1000;

    /// <summary>The helicopter wait, scaled by first aid (permille: 1000 fully staffed, 2000 without anybody).</summary>
    public static int RescueWaitPermille(WorldState state) => 1000 + ShortagePermille(state, StaffEffect.FirstAid);

    /// <summary>A minor crash's mood loss, scaled by first aid (permille).</summary>
    public static int MinorCrashMoodPermille(WorldState state) =>
        1000 + ShortagePermille(state, StaffEffect.FirstAid) * state.StaffRules.MinorCrashExtraMoodPermille / 1000;

    /// <summary>
    /// Mood guests lose this minute for want of cleaning: <see cref="StaffRules.CleaningMoodPerMinute"/> in as many of
    /// every 10 minutes as tenths are missing (deterministic, no randomness).
    /// </summary>
    public static int CleaningMoodLoss(WorldState state, long tick)
    {
        int shortage = ShortagePermille(state, StaffEffect.Cleaning);
        return shortage > 0 && (GameTime.MinuteOfDay(tick) % 10) * 100 < shortage ? state.StaffRules.CleaningMoodPerMinute : 0;
    }

    /// <summary>
    /// Demand from marketing (permille, 1000 = none): the first marketer brings their <see cref="StaffRole.CoversPer"/>,
    /// together n of them 2n/(n+1) times that; scaled by supervision.
    /// </summary>
    public static int MarketingPermille(WorldState state)
    {
        if (!state.StaffRules.Enabled) return 1000;
        int n = CountOf(state, StaffEffect.Marketing);
        if (n == 0) return 1000;
        long bonus = Covers(state, StaffEffect.Marketing) * 2 / (n + 1);
        return (int)(1000 + bonus * SupervisionPermille(state) / 1000);
    }

    /// <summary>The staff's wages per day (all roles).</summary>
    public static long WagesPerDay(WorldState state)
    {
        long wages = 0;
        foreach (var role in state.StaffRoles)
            wages += Count(state, role.Id) * role.WagePerDayCents;
        return wages;
    }

    /// <summary>What the park pays for its staff per day: the roles' wages, or the flat base staff with staff off.</summary>
    public static long DailyCostCents(WorldState state) =>
        state.StaffRules.Enabled ? WagesPerDay(state) : state.Rules.DailyUpkeepCents;
}
