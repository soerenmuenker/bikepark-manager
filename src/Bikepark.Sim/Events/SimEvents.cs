using Bikepark.Sim.Commands;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Trails;

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

public enum TurnAwayReason
{
    CannotAfford,
    ParkingFull,
}

public sealed record GuestTurnedAway(long Tick, TurnAwayReason Reason = TurnAwayReason.CannotAfford) : ISimEvent;

public enum GuestLeaveReason
{
    StayCompleted,
    Unhappy,
    ParkClosed,
    Tired,
}

public sealed record GuestLeft(long Tick, int GuestId, GuestLeaveReason Reason) : ISimEvent;

/// <summary>A guest started their lunch break (<paramref name="PaidCents"/> 0: they brought their own).</summary>
public sealed record GuestAteLunch(long Tick, int GuestId, long PaidCents) : ISimEvent;

public sealed record WayBuilt(long Tick, int WayId) : ISimEvent;

public sealed record WayDeleted(long Tick, int WayId) : ISimEvent;

public sealed record TrailFeaturePlaced(long Tick, int WayId, int FeatureId) : ISimEvent;

public sealed record TrailFeatureRemoved(long Tick, int WayId, int FeatureId) : ISimEvent;

public sealed record CrewHired(long Tick, int CrewId) : ISimEvent;

public sealed record CrewDismissed(long Tick, int CrewId) : ISimEvent;

public sealed record ToolBought(long Tick, string ToolId) : ISimEvent;

public sealed record WoodBought(long Tick, int Amount) : ISimEvent;

public sealed record JobQueued(long Tick, int JobId, JobKind Kind) : ISimEvent;

/// <summary>A job is done: its way or feature (if any) is built now. Title: what it was (<see cref="Jobs.Title"/>).</summary>
public sealed record JobCompleted(long Tick, int JobId, JobKind Kind, int WayId, int FeatureId, string Title) : ISimEvent;

public sealed record JobCancelled(long Tick, int JobId, JobKind Kind) : ISimEvent;

/// <summary>The crew cut a tree (it is gone now) and added its wood to the stock.</summary>
public sealed record TreeFelled(long Tick, int JobId, PointCm Tree, int Wood) : ISimEvent;

public sealed record RunStarted(long Tick, int GuestId, int TrailId) : ISimEvent;

/// <summary>A rider reached the end of a trail. Fun is the run's average, 0..1000.</summary>
public sealed record RunFinished(long Tick, int GuestId, int TrailId, int Minutes, int Fun) : ISimEvent;

public sealed record LiftBuilt(long Tick, int LiftId) : ISimEvent;

public sealed record LiftDeleted(long Tick, int LiftId) : ISimEvent;

public sealed record ParkingLotBuilt(long Tick, int ParkingLotId) : ISimEvent;

public sealed record ParkingLotDeleted(long Tick, int ParkingLotId) : ISimEvent;

/// <summary>A different bike access tier was booked; it starts at the next opening.</summary>
public sealed record BikeAccessBooked(long Tick, int LiftId, int TierIndex) : ISimEvent;

/// <summary>A booked tier came into effect.</summary>
public sealed record BikeAccessChanged(long Tick, int LiftId, int TierIndex, int BikeCarrierPermille) : ISimEvent;

public sealed record RiderQueued(long Tick, int GuestId, int LiftId, int QueueLength) : ISimEvent;

public sealed record RiderBoarded(long Tick, int GuestId, int LiftId, int WaitMinutes) : ISimEvent;

public sealed record RiderUnloaded(long Tick, int GuestId, int LiftId) : ISimEvent;

public sealed record DayReport(
    long Day,
    int Visitors,
    int TurnedAway,
    long RevenueCents,
    long ExpensesCents,
    long MoneyCents,
    long LiftFeesCents = 0,
    int LiftRides = 0,
    long WagesCents = 0,
    int WoodStock = 0,
    int Jobs = 0);

public sealed record DayEnded(long Tick, DayReport Report) : ISimEvent;
