using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Looks after the trails every minute: a trail whose worst segment fell below <see cref="WearRules.CloseBelowPermille"/>
/// closes (worn out) until a repair job finishes; trails set to <see cref="Way.Maintain"/> get a repair job once a segment
/// falls below <see cref="WearRules.MaintainBelowPermille"/>; minutes a trail is closed while the park is open are
/// counted. No randomness. Runs after the riders (who wear the trails) and before the crew.
/// </summary>
internal sealed class TrailCareSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        bool open = ParkSchedule.IsOpen(state, ctx.Tick);
        foreach (var trail in state.Ways)
        {
            if (trail.Kind != WayKind.Trail || !trail.Built) continue;
            if (open && !trail.IsRideable) trail.Stats.ClosedMinutes++;
            if (trail.Condition.Count == 0 || !ctx.Network.TryGetGeometry(trail.Id, out var geometry)) continue; // unworn: nothing to check

            int worst = TrailCondition.WorstPermille(trail, geometry.Segments.Count);
            if (!trail.WornOut && worst < state.WearRules.CloseBelowPermille)
            {
                bool wasRideable = trail.IsRideable;
                trail.WornOut = true;
                if (wasRideable) ctx.Publish(new TrailClosed(ctx.Tick, trail.Id, WornOut: true));
            }
            if (trail.Maintain && worst < state.WearRules.MaintainBelowPermille && Jobs.ForRepair(state, trail.Id) is null)
                Jobs.QueueRepair(ctx, trail, geometry);
        }
    }
}
