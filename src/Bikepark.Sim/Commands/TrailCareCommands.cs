using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Commands;

internal static class TrailCare
{
    /// <summary>The built trail with this id, or a rejection reason.</summary>
    public static string? CheckTrail(SimContext ctx, int wayId, out Way? trail)
    {
        trail = ctx.State.Ways.FirstOrDefault(w => w.Id == wayId);
        if (trail is null || trail.Kind != WayKind.Trail) return "No such trail.";
        return trail.Built ? null : "The trail isn't built yet.";
    }
}

/// <summary>
/// Sends the crew to repair a worn feature: a repair job at the front of the queue (<see cref="Workers"/> = how many
/// workers, 0 = as many as allowed). The trail is closed from the moment the crew starts until the feature is perfect again.
/// </summary>
public sealed record RepairFeatureCommand(int WayId, int FeatureId, int Workers = 0) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        if (TrailCare.CheckTrail(ctx, WayId, out var trail) is { } error) return error;
        var feature = trail!.Features.FirstOrDefault(f => f.Id == FeatureId);
        if (feature is null || !feature.Built) return "No such feature.";
        if (Jobs.ForRepair(ctx.State, FeatureId) is not null) return "A repair is already planned.";
        if (Workers < 0 || Workers > ctx.State.CrewRules.MaxWorkersPerJob) return $"Send 0 to {ctx.State.CrewRules.MaxWorkersPerJob} workers.";
        return feature.Condition < TrailCondition.Perfect ? null : "The feature is in perfect condition.";
    }

    public void Apply(SimContext ctx)
    {
        var trail = ctx.State.Ways.First(w => w.Id == WayId);
        var feature = trail.Features.First(f => f.Id == FeatureId);
        Jobs.QueueRepair(ctx, trail, feature, TrailFeatures.FindType(ctx.State.TrailFeatureTypes, feature.TypeId)!, Workers);
    }
}

/// <summary>Closes a trail to riders (those on it finish their run) or opens it again (a worn-out trail stays closed until repaired).</summary>
public sealed record SetTrailClosedCommand(int WayId, bool Closed) : ICommand
{
    public string? Validate(SimContext ctx) => TrailCare.CheckTrail(ctx, WayId, out _);

    public void Apply(SimContext ctx)
    {
        var trail = ctx.State.Ways.First(w => w.Id == WayId);
        bool wasRideable = trail.IsRideable;
        trail.Closed = Closed;
        if (wasRideable && !trail.IsRideable) ctx.Publish(new TrailClosed(ctx.Tick, WayId, TrailClosedReason.Player));
        else if (!wasRideable && trail.IsRideable) ctx.Publish(new TrailReopened(ctx.Tick, WayId));
    }
}
