using Bikepark.Sim.Core;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>Where the park is in its day (<see cref="ParkSchedule.Phase"/>).</summary>
public enum DayPhase
{
    /// <summary>Outside opening hours: no guests arrive, the lifts stand still and the crew is off.</summary>
    Closed,

    Open,

    /// <summary>After closing while guests are still in the park: lifts only bring up whoever is aboard, riders do their last run.</summary>
    LastRides,
}

/// <summary>
/// The park's daily timetable, as pure functions of the state and a tick: the opening hours (guests, lifts and crew all
/// keep them), followed by the last rides, which last until the last rider is down. The host uses <see cref="IsQuiet"/> and <see cref="NextWakeTick"/> to skip through
/// dead hours (it still steps every tick).
/// </summary>
public static class ParkSchedule
{
    /// <summary>Opening hours (a bankrupt park never opens again).</summary>
    public static bool IsOpen(WorldState state, long tick)
    {
        if (state.Park.Bankrupt) return false;
        int minute = GameTime.MinuteOfDay(tick);
        return minute >= state.Rules.OpenMinute && minute < state.Rules.CloseMinute;
    }

    /// <summary>
    /// From closing until midnight: no new guests, nobody starts a new lap, riders on a lap finish it. There is no fixed
    /// length; whoever is left at midnight goes home.
    /// </summary>
    public static bool IsLastRides(WorldState state, long tick) => GameTime.MinuteOfDay(tick) >= state.Rules.CloseMinute;

    /// <summary>Guests may be in the park: open or last rides.</summary>
    public static bool GuestsAllowed(WorldState state, long tick) => IsOpen(state, tick) || IsLastRides(state, tick);

    /// <summary>
    /// True if the lift runs this minute: open, or last rides (or closed by the player) while riders are still aboard (nobody
    /// boards after closing).
    /// </summary>
    public static bool LiftRunning(WorldState state, Lift lift, long tick)
    {
        if (!lift.InService || lift.IsStopped(tick)) return false;
        if (IsOpen(state, tick) && !lift.Closed) return true;
        if (!lift.Closed && !IsLastRides(state, tick)) return false;
        return state.Guests.Any(g => g.Activity == RiderActivity.OnLift && g.Route.Count > g.LegIndex
                                                             && g.Route[g.LegIndex].WayId == lift.Id);
    }

    public static DayPhase Phase(WorldState state, long tick) =>
        IsOpen(state, tick) ? DayPhase.Open
        : IsLastRides(state, tick) && AwakeGuests(state) > 0 ? DayPhase.LastRides
        : DayPhase.Closed;

    /// <summary>Guests in the park: everyone but campers asleep at their campground.</summary>
    public static int AwakeGuests(WorldState state) => state.Guests.Count(g => g.Activity != RiderActivity.Camping);

    /// <summary>
    /// Dead hours the host may fast-forward through: the park is closed and empty (the last rides are over; a seriously
    /// injured rider waiting for the helicopter is still in it; campers asleep at the campground are not).
    /// </summary>
    public static bool IsQuiet(WorldState state, long tick) => !IsOpen(state, tick) && AwakeGuests(state) == 0;

    /// <summary>The next opening after <paramref name="tick"/>.</summary>
    public static long NextWakeTick(WorldState state, long tick)
    {
        long target = GameTime.Day(tick) * GameTime.MinutesPerDay + state.Rules.OpenMinute;
        return target > tick ? target : target + GameTime.MinutesPerDay;
    }
}
