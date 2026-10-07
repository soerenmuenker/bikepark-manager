using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Looks after the trails every minute (no randomness; runs after the riders who wear the features, before the crew):
/// <list type="bullet">
///   <item>a built feature below <see cref="WearRules.WarnBelowPermille"/> raises a <see cref="FeatureWarning"/> once;</item>
///   <item>a trail with a feature at 0 closes (<see cref="Way.WornOut"/>) until repairs leave none at 0 — the warning is
///   raised again then, so the player can send the crew;</item>
///   <item>a trail is closed while the crew is repairing one of its features (<see cref="Way.Repairing"/>);</item>
///   <item>minutes a trail is closed while the park is open are counted.</item>
/// </list>
/// Nothing is repaired on its own: the player sends the crew (<see cref="Commands.RepairFeatureCommand"/>).
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
            if (trail.Features.Count > 0)
                UpdateTrail(ctx, trail);
            if (open && !trail.IsRideable) trail.Stats.ClosedMinutes++;
        }
    }

    private static void UpdateTrail(SimContext ctx, Way trail)
    {
        var state = ctx.State;
        var rules = state.WearRules;

        foreach (var feature in trail.Features)
        {
            if (!feature.Built || feature.Condition >= TrailCondition.Perfect) continue;
            int permille = TrailCondition.Permille(feature);
            if (!feature.Warned && permille < rules.WarnBelowPermille)
            {
                feature.Warned = true;
                ctx.Publish(new FeatureWarning(ctx.Tick, trail.Id, feature.Id, permille));
            }
            if (feature.Condition <= 0 && !trail.WornOut)
            {
                bool wasRideable = trail.IsRideable;
                trail.WornOut = true;
                if (wasRideable) ctx.Publish(new TrailClosed(ctx.Tick, trail.Id, TrailClosedReason.WornOut));
                ctx.Publish(new FeatureWarning(ctx.Tick, trail.Id, feature.Id, 0)); // pop up again: the trail is closed now
            }
        }

        bool repairing = Jobs.ForTrailRepair(state, trail.Id) is { } job && Jobs.IsBeingRepaired(state, job);
        if (repairing != trail.Repairing)
        {
            bool wasRideable = trail.IsRideable;
            trail.Repairing = repairing;
            if (wasRideable && !trail.IsRideable) ctx.Publish(new TrailClosed(ctx.Tick, trail.Id, TrailClosedReason.Repair));
            else if (!wasRideable && trail.IsRideable) ctx.Publish(new TrailReopened(ctx.Tick, trail.Id));
        }
    }
}
