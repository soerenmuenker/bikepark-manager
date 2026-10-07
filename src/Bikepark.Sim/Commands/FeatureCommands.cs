using Bikepark.Sim.Events;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Commands;

/// <summary>
/// Places a feature (berm, jump, wood feature) on a trail, starting at a distance along it. Instant and free in Phase
/// 4.1; <see cref="FeaturePlanner"/> validates. Shapes don't change, so riders on the trail keep their route.
/// </summary>
public sealed record PlaceTrailFeatureCommand(int WayId, string TypeId, long DistanceCm) : ICommand
{
    public string? Validate(SimContext ctx) =>
        TypeId is null ? "No feature type given." : Plan(ctx).FirstError;

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var way = state.Ways.First(w => w.Id == WayId);
        var feature = new TrailFeature { Id = state.AllocateEntityId(), TypeId = TypeId, DistanceCm = DistanceCm };
        int index = way.Features.FindIndex(f => f.DistanceCm > DistanceCm);
        way.Features.Insert(index < 0 ? way.Features.Count : index, feature);
        state.WaysRevision++;
        ctx.Publish(new TrailFeaturePlaced(ctx.Tick, WayId, feature.Id));
    }

    private FeaturePlan Plan(SimContext ctx) =>
        FeaturePlanner.Plan(ctx.Network, ctx.State.TrailRules, ctx.State.TrailFeatureTypes, WayId, TypeId, DistanceCm);
}

/// <summary>Removes a feature from a trail.</summary>
public sealed record RemoveTrailFeatureCommand(int WayId, int FeatureId) : ICommand
{
    public string? Validate(SimContext ctx) =>
        ctx.State.Ways.FirstOrDefault(w => w.Id == WayId)?.Features.Any(f => f.Id == FeatureId) == true ? null : "No such feature.";

    public void Apply(SimContext ctx)
    {
        ctx.State.Ways.First(w => w.Id == WayId).Features.RemoveAll(f => f.Id == FeatureId);
        ctx.State.WaysRevision++;
        ctx.Publish(new TrailFeatureRemoved(ctx.Tick, WayId, FeatureId));
    }
}
