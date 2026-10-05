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
}

public sealed record GuestLeft(long Tick, int GuestId, GuestLeaveReason Reason) : ISimEvent;

public sealed record DayReport(
    long Day,
    int Visitors,
    int TurnedAway,
    long RevenueCents,
    long ExpensesCents,
    long MoneyCents);

public sealed record DayEnded(long Tick, DayReport Report) : ISimEvent;
