using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Commands;

/// <summary>
/// Plans a feature (berm, jump, wood feature) on a trail, starting at a distance along it, and queues the crew job
/// that builds it (with the type's wood); <paramref name="Instant"/> (debug) builds it at once. <see cref="FeaturePlanner"/>
/// validates. Shapes don't change, so riders on the trail keep their route.
/// </summary>
public sealed record PlaceTrailFeatureCommand(int WayId, string TypeId, long DistanceCm, bool Instant = false) : ICommand
{
    public string? Validate(SimContext ctx) =>
        TypeId is null ? "No feature type given." : Plan(ctx).FirstError;

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var way = state.Ways.First(w => w.Id == WayId);
        var feature = new TrailFeature { Id = state.AllocateEntityId(), TypeId = TypeId, DistanceCm = DistanceCm, Built = Instant };
        if (!Instant)
            Jobs.QueueFeature(ctx, WayId, feature, TrailFeatures.FindType(state.TrailFeatureTypes, TypeId)!);
        int index = way.Features.FindIndex(f => f.DistanceCm > DistanceCm);
        way.Features.Insert(index < 0 ? way.Features.Count : index, feature);
        state.WaysRevision++;
        ctx.Publish(new TrailFeaturePlaced(ctx.Tick, WayId, feature.Id));
    }

    private FeaturePlan Plan(SimContext ctx) =>
        FeaturePlanner.Plan(ctx.Network, ctx.State.TrailRules, ctx.State.TrailFeatureTypes, WayId, TypeId, DistanceCm);
}

/// <summary>Removes a feature from a trail (a planned one: cancels its job, wood it took goes back to the stock).</summary>
public sealed record RemoveTrailFeatureCommand(int WayId, int FeatureId) : ICommand
{
    public string? Validate(SimContext ctx) =>
        ctx.State.Ways.FirstOrDefault(w => w.Id == WayId)?.Features.Any(f => f.Id == FeatureId) == true ? null : "No such feature.";

    public void Apply(SimContext ctx)
    {
        if (Jobs.ForFeature(ctx.State, FeatureId) is { } job)
            Jobs.Cancel(ctx, job);
        ctx.State.Ways.First(w => w.Id == WayId).Features.RemoveAll(f => f.Id == FeatureId);
        ctx.State.WaysRevision++;
        ctx.Publish(new TrailFeatureRemoved(ctx.Tick, WayId, FeatureId));
    }
}
