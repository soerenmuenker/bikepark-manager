using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Runs the lifts. At opening time booked bike access tiers take effect. While the lift runs
/// (<see cref="ParkSchedule.LiftRunning"/>: open, warm-up before opening, last rides while riders wait) each lift sends up a
/// carrier every <see cref="LiftType.IntervalSeconds"/>; bike-equipped carriers (<see cref="LiftMath.IsBikeCarrier"/>)
/// take up to <see cref="LiftType.BikesPerCarrier"/> riders from the front of the queue, who then ride up
/// (<see cref="RiderSystem"/> moves them). Runs after <see cref="RiderSystem"/>, so riders reaching the station board in
/// the same tick if there is room.
/// </summary>
internal sealed class LiftSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Lifts.Count == 0) return;

        if (GameTime.MinuteOfDay(ctx.Tick) == state.Rules.OpenMinute)
            foreach (var lift in state.Lifts)
                ApplyBookedTier(ctx, lift);

        foreach (var lift in state.Lifts)
        {
            if (lift.BikeCarrierPermille <= 0 && lift.Queue.Count > 0)
                ReleaseQueue(ctx, lift);
            if (ParkSchedule.LiftRunning(state, lift, ctx.Tick))
                Dispatch(ctx, lift);
            lift.Stats.MaxQueueToday = Math.Max(lift.Stats.MaxQueueToday, lift.Queue.Count);
            lift.Stats.MaxQueue = Math.Max(lift.Stats.MaxQueue, lift.Queue.Count);
        }
    }

    internal static void JoinQueue(SimContext ctx, Lift lift, Guest guest)
    {
        guest.Activity = RiderActivity.Queuing;
        guest.QueueSinceTick = ctx.Tick;
        lift.Queue.Add(guest.Id);
        ctx.Publish(new RiderQueued(ctx.Tick, guest.Id, lift.Id, lift.Queue.Count));
    }

    /// <summary>Removes a rider from whatever queue they are in (no-op if none).</summary>
    internal static void LeaveQueue(WorldState state, Guest guest)
    {
        foreach (var lift in state.Lifts)
            lift.Queue.Remove(guest.Id);
    }

    private static void ApplyBookedTier(SimContext ctx, Lift lift)
    {
        if (lift.BikeAccess is not { PendingTierIndex: { } tier } access) return;
        var op = LiftNetwork.FindOperator(ctx.State, lift.OperatorId);
        access.PendingTierIndex = null;
        if (op is null || tier < 0 || tier >= op.BikeAccessTiers.Count) return;
        access.TierIndex = tier;
        lift.BikeCarrierPermille = op.BikeAccessTiers[tier].BikeCarrierPermille;
        ctx.Publish(new BikeAccessChanged(ctx.Tick, lift.Id, tier, lift.BikeCarrierPermille));
    }

    private static void Dispatch(SimContext ctx, Lift lift)
    {
        var state = ctx.State;
        var type = LiftNetwork.FindType(state, lift.TypeId);
        if (type is null) return;

        int intervalMs = type.IntervalSeconds * 1000;
        int budget = lift.DispatchRemainderMs + 60_000;
        int carriers = budget / intervalMs;
        lift.DispatchRemainderMs = budget % intervalMs;

        for (int c = 0; c < carriers; c++)
        {
            long k = lift.CarriersDispatched++;
            if (!LiftMath.IsBikeCarrier(k, lift.BikeCarrierPermille)) continue;
            for (int seat = 0; seat < type.BikesPerCarrier && lift.Queue.Count > 0; seat++)
            {
                int guestId = lift.Queue[0];
                lift.Queue.RemoveAt(0);
                var guest = state.Guests.FirstOrDefault(g => g.Id == guestId);
                if (guest is null || guest.Activity != RiderActivity.Queuing) continue;
                int wait = (int)(ctx.Tick - guest.QueueSinceTick);
                guest.Activity = RiderActivity.OnLift;
                lift.Stats.Riders++;
                lift.Stats.RidersToday++;
                lift.Stats.SumWaitMinutes += wait;
                ctx.Publish(new RiderBoarded(ctx.Tick, guest.Id, lift.Id, wait));
            }
        }
    }

    /// <summary>No carrier takes bikes any more: riders in the queue go back to the valley station and choose again.</summary>
    private static void ReleaseQueue(SimContext ctx, Lift lift)
    {
        var state = ctx.State;
        foreach (int guestId in lift.Queue.ToList())
        {
            var guest = state.Guests.FirstOrDefault(g => g.Id == guestId);
            if (guest is null) continue;
            guest.Route = [];
            guest.RouteProgressCm = 0;
            guest.LegIndex = 0;
            guest.TrailId = 0;
            guest.Activity = RiderActivity.Idle;
            guest.LocationWayId = 0;
            guest.LocationCm = 0;
            guest.LocationHubId = lift.Valley.Id;
        }
        lift.Queue.Clear();
    }
}
