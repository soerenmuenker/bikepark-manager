using Bikepark.Sim.Events;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Crew;

/// <summary>Creating, finishing and cancelling crew jobs (shared by the commands and <see cref="Systems.JobSystem"/>).</summary>
public static class Jobs
{
    public static Job? Find(WorldState state, int jobId) => state.Jobs.FirstOrDefault(j => j.Id == jobId);

    /// <summary>The job building this way, if it is planned.</summary>
    public static Job? ForWay(WorldState state, int wayId) =>
        state.Jobs.FirstOrDefault(j => j.Kind == JobKind.BuildWay && j.WayId == wayId);

    /// <summary>The job repairing this trail, if one is queued.</summary>
    public static Job? ForRepair(WorldState state, int wayId) =>
        state.Jobs.FirstOrDefault(j => j.Kind == JobKind.RepairTrail && j.WayId == wayId);

    public static Job? ForFeature(WorldState state, int featureId) =>
        state.Jobs.FirstOrDefault(j => j.Kind == JobKind.BuildFeature && j.FeatureId == featureId);

    /// <summary>What the job is for, e.g. "Trail Flow Country", "Berm on Flow Country at 40 m", "Fell 32 trees".</summary>
    public static string Title(WorldState state, Job job)
    {
        var way = state.Ways.FirstOrDefault(w => w.Id == job.WayId);
        switch (job.Kind)
        {
            case JobKind.BuildWay:
                return way is null ? "Way" : $"{(way.Kind == WayKind.Trail ? "Trail" : "Path")} {way.Name}";
            case JobKind.BuildFeature:
                var feature = way?.Features.FirstOrDefault(f => f.Id == job.FeatureId);
                var type = feature is null ? null : TrailFeatures.FindType(state.TrailFeatureTypes, feature.TypeId);
                return $"{type?.Name ?? "Feature"} on {way?.Name ?? "a trail"}{(feature is null ? "" : $" at {feature.DistanceCm / 100} m")}";
            case JobKind.RepairTrail:
                return $"Repair {way?.Name ?? "a trail"}";
            default:
                return $"Fell {job.Trees.Count} trees";
        }
    }

    /// <summary>Appends a job to the end of the queue.</summary>
    public static Job Queue(SimContext ctx, Job job)
    {
        job.Id = ctx.State.AllocateEntityId();
        ctx.State.Jobs.Add(job);
        ctx.Publish(new JobQueued(ctx.Tick, job.Id, job.Kind));
        return job;
    }

    /// <summary>Queues building a planned way: fell the trees in its corridor, then dig it.</summary>
    public static Job QueueWay(SimContext ctx, Way way, WayGeometry geometry)
    {
        var state = ctx.State;
        var trees = Forest.TreesAlong(ctx.Terrain, ctx.Network, Forest.Taken(state), geometry, WayNetwork.CorridorWidth(state.TrailRules, way.Kind));
        return Queue(ctx, new Job
        {
            Kind = JobKind.BuildWay,
            WayId = way.Id,
            Trees = trees,
            WorkMinutes = WorkCosts.WayWorkMinutes(state.CrewRules, state.TrailRules, way.Kind, geometry),
            MainWorkType = WorkType.Digging,
        });
    }

    /// <summary>Queues repairing a trail's wear (the work is fixed now, from its condition at this moment).</summary>
    public static Job QueueRepair(SimContext ctx, Way way, WayGeometry geometry) =>
        Queue(ctx, new Job
        {
            Kind = JobKind.RepairTrail,
            WayId = way.Id,
            WorkMinutes = WorkCosts.RepairMinutes(ctx.State.CrewRules, way, geometry.Segments.Count),
            MainWorkType = WorkType.Digging,
        });

    public static Job QueueFeature(SimContext ctx, int wayId, TrailFeature feature, TrailFeatureType type) =>
        Queue(ctx, new Job
        {
            Kind = JobKind.BuildFeature,
            WayId = wayId,
            FeatureId = feature.Id,
            WorkMinutes = type.WorkMinutes,
            MainWorkType = WorkCosts.FeatureWorkType(type),
            Wood = type.Wood,
        });

    /// <summary>Removes a job: its workers stop, wood it took goes back to the stock. Trees felled so far stay felled.</summary>
    public static void Cancel(SimContext ctx, Job job)
    {
        var state = ctx.State;
        if (job.WoodTaken && job.Wood > 0)
        {
            state.WoodStock += job.Wood;
            state.CrewStats.WoodUsed -= job.Wood;
        }
        Release(state, job);
        ctx.Publish(new JobCancelled(ctx.Tick, job.Id, job.Kind));
    }

    /// <summary>Cuts the job's next tree: it disappears and its wood goes into the stock.</summary>
    public static void FellNext(SimContext ctx, Job job)
    {
        var state = ctx.State;
        var tree = job.Trees[job.TreesFelled++];
        state.FelledTrees.Add(tree);
        int wood = state.CrewRules.WoodPerTree;
        state.WoodStock += wood;
        state.CrewStats.TreesFelled++;
        state.CrewStats.WoodFelled += wood;
        ctx.Publish(new TreeFelled(ctx.Tick, job.Id, tree, wood));
    }

    /// <summary>Finishes a job: its way or feature is built (the network re-derives), the crew moves on.</summary>
    public static void Complete(SimContext ctx, Job job)
    {
        var state = ctx.State;
        switch (job.Kind)
        {
            case JobKind.BuildWay when state.Ways.FirstOrDefault(w => w.Id == job.WayId) is { } way:
                way.Built = true;
                state.WaysRevision++;
                // Its corridor is clear now; those trees don't need to be listed any more.
                if (job.Trees.Count > 0)
                {
                    var own = new HashSet<PointCm>(job.Trees);
                    state.FelledTrees.RemoveAll(own.Contains);
                }
                break;
            case JobKind.BuildFeature when state.Ways.FirstOrDefault(w => w.Id == job.WayId)?.Features.FirstOrDefault(f => f.Id == job.FeatureId) is { } feature:
                feature.Built = true;
                state.WaysRevision++;
                break;
            case JobKind.RepairTrail when state.Ways.FirstOrDefault(w => w.Id == job.WayId) is { } trail:
                TrailCondition.Restore(trail);
                trail.Stats.Repairs++;
                if (trail.WornOut)
                {
                    trail.WornOut = false;
                    if (trail.IsRideable) ctx.Publish(new TrailReopened(ctx.Tick, trail.Id));
                }
                break;
        }
        state.CrewStats.JobsCompleted++;
        string title = Title(state, job);
        Release(state, job);
        ctx.Publish(new JobCompleted(ctx.Tick, job.Id, job.Kind, job.WayId, job.FeatureId, title));
    }

    private static void Release(WorldState state, Job job)
    {
        foreach (var member in state.Crew)
            if (member.JobId == job.Id)
                member.JobId = 0;
        state.Jobs.Remove(job);
    }
}
