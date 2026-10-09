using Bikepark.Sim.Core;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>Where the park is in its day (<see cref="ParkSchedule.Phase"/>).</summary>
public enum DayPhase
{
    /// <summary>Nothing scheduled: park closed, crew off shift.</summary>
    Night,

    /// <summary>Before opening: the crew is on shift or the lifts are warming up.</summary>
    PreOpening,

    Open,

    /// <summary>Just after closing: riders on a lap finish it, the lifts empty their queues (<see cref="ParkRules.LastRideMinutes"/>).</summary>
    LastRides,

    /// <summary>After closing (and the last rides): the crew is still on shift or doing overtime.</summary>
    AfterHours,
}

/// <summary>
/// The park's daily timetable, as pure functions of the state and a tick: opening hours, last rides, lift warm-up, crew
/// shift and overtime. The host uses <see cref="IsQuiet"/> and <see cref="NextWakeTick"/> to skip through dead hours
/// (it still steps every tick).
/// </summary>
public static class ParkSchedule
{
    public static bool IsOpen(WorldState state, long tick)
    {
        int minute = GameTime.MinuteOfDay(tick);
        return minute >= state.Rules.OpenMinute && minute < state.Rules.CloseMinute;
    }

    /// <summary>After closing, while riders on a lap may still finish it.</summary>
    public static bool IsLastRides(WorldState state, long tick)
    {
        int minute = GameTime.MinuteOfDay(tick);
        return minute >= state.Rules.CloseMinute && minute < state.Rules.CloseMinute + state.Rules.LastRideMinutes;
    }

    /// <summary>Guests may be in the park: open or last rides.</summary>
    public static bool GuestsAllowed(WorldState state, long tick) => IsOpen(state, tick) || IsLastRides(state, tick);

    /// <summary>The lifts run empty before opening.</summary>
    public static bool IsLiftWarmup(WorldState state, long tick)
    {
        int minute = GameTime.MinuteOfDay(tick);
        return minute >= state.Rules.OpenMinute - state.Rules.LiftWarmupMinutes && minute < state.Rules.OpenMinute;
    }

    /// <summary>The crew's regular shift (only meaningful with crew).</summary>
    public static bool IsCrewShift(WorldState state, long tick)
    {
        int minute = GameTime.MinuteOfDay(tick);
        return minute >= state.CrewRules.WorkStartMinute && minute < state.CrewRules.WorkEndMinute;
    }

    /// <summary>The overtime window right after the shift.</summary>
    public static bool IsCrewOvertime(WorldState state, long tick)
    {
        int minute = GameTime.MinuteOfDay(tick);
        return minute >= state.CrewRules.WorkEndMinute && minute < state.CrewRules.WorkEndMinute + state.CrewRules.OvertimeMinutes;
    }

    /// <summary>Overtime minutes left after this one (0 outside overtime).</summary>
    public static int OvertimeLeft(WorldState state, long tick) => IsCrewOvertime(state, tick)
        ? state.CrewRules.WorkEndMinute + state.CrewRules.OvertimeMinutes - GameTime.MinuteOfDay(tick)
        : 0;

    /// <summary>True if any worker is at work (on shift, or in overtime on a job).</summary>
    public static bool CrewAtWork(WorldState state, long tick) =>
        state.Crew.Count > 0 && (IsCrewShift(state, tick) || state.Crew.Any(m => m.JobId != 0));

    /// <summary>True if the lift carries anyone or runs this minute: open, warm-up, or last rides while riders still wait or ride.</summary>
    public static bool LiftRunning(WorldState state, Lift lift, long tick)
    {
        if (!lift.InService) return false;
        if (IsOpen(state, tick) || IsLiftWarmup(state, tick)) return true;
        if (!IsLastRides(state, tick)) return false;
        return lift.Queue.Count > 0 || state.Guests.Any(g => g.Activity == RiderActivity.OnLift && g.Route.Count > g.LegIndex
                                                             && g.Route[g.LegIndex].WayId == lift.Id);
    }

    public static DayPhase Phase(WorldState state, long tick)
    {
        if (IsOpen(state, tick)) return DayPhase.Open;
        if (IsLastRides(state, tick)) return DayPhase.LastRides;
        int minute = GameTime.MinuteOfDay(tick);
        bool beforeOpening = minute < state.Rules.OpenMinute;
        if (beforeOpening && (IsLiftWarmup(state, tick) && state.Lifts.Count > 0 || CrewAtWork(state, tick)))
            return DayPhase.PreOpening;
        if (!beforeOpening && CrewAtWork(state, tick))
            return DayPhase.AfterHours;
        return state.Guests.Count > 0 ? DayPhase.AfterHours : DayPhase.Night;
    }

    /// <summary>
    /// Dead hours the host may fast-forward through: the park is closed and empty, no worker is at work and no lift is
    /// warming up.
    /// </summary>
    public static bool IsQuiet(WorldState state, long tick) => Phase(state, tick) == DayPhase.Night;

    /// <summary>The first tick after <paramref name="tick"/> where something is scheduled: crew shift start (with crew) or lift warm-up / opening.</summary>
    public static long NextWakeTick(WorldState state, long tick)
    {
        long next = NextAt(tick, state.Rules.OpenMinute - (state.Lifts.Count > 0 ? state.Rules.LiftWarmupMinutes : 0));
        if (state.Crew.Count > 0)
            next = Math.Min(next, NextAt(tick, state.CrewRules.WorkStartMinute));
        return next;
    }

    /// <summary>The next tick after <paramref name="tick"/> at the given minute of the day.</summary>
    private static long NextAt(long tick, int minuteOfDay)
    {
        long target = GameTime.Day(tick) * GameTime.MinutesPerDay + minuteOfDay;
        return target > tick ? target : target + GameTime.MinutesPerDay;
    }
}
