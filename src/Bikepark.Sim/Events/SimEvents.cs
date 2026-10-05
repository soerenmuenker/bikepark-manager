using Bikepark.Sim.Commands;

namespace Bikepark.Sim.Events;

/// <summary>Something that happened in the simulation, for presentation only. Events are not saved.</summary>
public interface ISimEvent
{
    long Tick { get; }
}

public sealed record CommandApplied(long Tick, ICommand Command) : ISimEvent;

public sealed record CommandRejected(long Tick, ICommand Command, string Reason) : ISimEvent;

public sealed record ParkOpened(long Tick) : ISimEvent;

public sealed record ParkClosed(long Tick) : ISimEvent;

public sealed record GuestArrived(long Tick, int GuestId) : ISimEvent;

public sealed record GuestTurnedAway(long Tick) : ISimEvent;

public enum GuestLeaveReason
{
    StayCompleted,
    Unhappy,
    ParkClosed,
    Tired,
}

public sealed record GuestLeft(long Tick, int GuestId, GuestLeaveReason Reason) : ISimEvent;

public sealed record WayBuilt(long Tick, int WayId) : ISimEvent;

public sealed record WayDeleted(long Tick, int WayId) : ISimEvent;

public sealed record RunStarted(long Tick, int GuestId, int TrailId) : ISimEvent;

/// <summary>A rider reached the end of a trail. Fun is the run's average, 0..1000.</summary>
public sealed record RunFinished(long Tick, int GuestId, int TrailId, int Minutes, int Fun) : ISimEvent;

public sealed record DayReport(
    long Day,
    int Visitors,
    int TurnedAway,
    long RevenueCents,
    long ExpensesCents,
    long MoneyCents);

public sealed record DayEnded(long Tick, DayReport Report) : ISimEvent;
