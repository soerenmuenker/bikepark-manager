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

/// <summary>Queues a crew job that repairs a trail's wear (see <see cref="Jobs.QueueRepair"/>).</summary>
public sealed record RepairTrailCommand(int WayId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        if (TrailCare.CheckTrail(ctx, WayId, out var trail) is { } error) return error;
        if (Jobs.ForRepair(ctx.State, WayId) is not null) return "A repair is already planned.";
        return trail!.Condition.Any(c => c < TrailCondition.Perfect) ? null : "The trail is in perfect condition.";
    }

    public void Apply(SimContext ctx)
    {
        var trail = ctx.State.Ways.First(w => w.Id == WayId);
        Jobs.QueueRepair(ctx, trail, ctx.Network.Geometry(WayId));
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
        if (wasRideable && !trail.IsRideable) ctx.Publish(new TrailClosed(ctx.Tick, WayId, WornOut: false));
        else if (!wasRideable && trail.IsRideable) ctx.Publish(new TrailReopened(ctx.Tick, WayId));
    }
}

/// <summary>Turns automatic maintenance of a trail on or off (on: the crew gets a repair job when it wears).</summary>
public sealed record SetTrailMaintainCommand(int WayId, bool Maintain) : ICommand
{
    public string? Validate(SimContext ctx) => TrailCare.CheckTrail(ctx, WayId, out _);

    public void Apply(SimContext ctx) => ctx.State.Ways.First(w => w.Id == WayId).Maintain = Maintain;
}
