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

    /// <summary>The job repairing this feature, if one is queued.</summary>
    public static Job? ForRepair(WorldState state, int featureId) =>
        state.Jobs.FirstOrDefault(j => j.Kind == JobKind.RepairFeature && j.FeatureId == featureId);

    /// <summary>The job repairing any feature of this trail, if one is queued.</summary>
    public static Job? ForTrailRepair(WorldState state, int wayId) =>
        state.Jobs.FirstOrDefault(j => j.Kind == JobKind.RepairFeature && j.WayId == wayId);

    /// <summary>
    /// A job on one of this trail's features (building or repairing) that the crew has started or been assigned to: the
    /// trail is closed. The work itself only begins once no rider is on the trail any more (<see cref="IsWaitingForRiders"/>).
    /// </summary>
    public static Job? StartedFeatureWork(WorldState state, int wayId) =>
        state.Jobs.FirstOrDefault(j => j.Kind is JobKind.RepairFeature or JobKind.BuildFeature && j.WayId == wayId && IsStarted(state, j));

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
            case JobKind.RepairFeature:
                var worn = way?.Features.FirstOrDefault(f => f.Id == job.FeatureId);
                var wornType = worn is null ? null : TrailFeatures.FindType(state.TrailFeatureTypes, worn.TypeId);
                return $"Repair {wornType?.Name ?? "feature"} on {way?.Name ?? "a trail"}{(worn is null ? "" : $" at {worn.DistanceCm / 100} m")}";
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

    /// <summary>
    /// Queues repairing a worn feature at the front of the queue (the work is fixed now, from its condition at this moment).
    /// <paramref name="workers"/> is how many workers the player sends (0 = the rules' maximum).
    /// </summary>
    public static Job QueueRepair(SimContext ctx, Way way, TrailFeature feature, TrailFeatureType type, int workers)
    {
        var job = new Job
        {
            Kind = JobKind.RepairFeature,
            WayId = way.Id,
            FeatureId = feature.Id,
            Workers = workers,
            WorkMinutes = WorkCosts.RepairMinutes(ctx.State.CrewRules, type, feature),
            MainWorkType = WorkCosts.RepairWorkType(type),
        };
        Queue(ctx, job);
        ctx.State.Jobs.Remove(job);
        ctx.State.Jobs.Insert(0, job);
        return job;
    }

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
        if (job.Kind is JobKind.RepairFeature or JobKind.BuildFeature && state.Ways.FirstOrDefault(w => w.Id == job.WayId) is { } trail)
            Reopen(ctx, trail);
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
                Release(state, job);
                Reopen(ctx, state.Ways.First(w => w.Id == job.WayId));
                break;
            case JobKind.RepairFeature when state.Ways.FirstOrDefault(w => w.Id == job.WayId) is { } trail:
                if (trail.Features.FirstOrDefault(f => f.Id == job.FeatureId) is { } repaired)
                    TrailCondition.Restore(repaired);
                trail.Stats.Repairs++;
                Release(state, job);
                Reopen(ctx, trail);
                break;
        }
        state.CrewStats.JobsCompleted++;
        string title = Title(state, job);
        Release(state, job);
        ctx.Publish(new JobCompleted(ctx.Tick, job.Id, job.Kind, job.WayId, job.FeatureId, title));
    }

    /// <summary>
    /// After feature work (or cancelled work): the trail is no longer closed for it and, if no feature is worn out any more,
    /// no longer worn out. Publishes <see cref="TrailReopened"/> when riders may use it again.
    /// </summary>
    public static void Reopen(SimContext ctx, Way trail)
    {
        bool wasRideable = trail.IsRideable;
        trail.Repairing = RefreshUnderWork(ctx.State, trail, ctx.Tick);
        if (trail.WornOut && !trail.Features.Any(f => f.Built && f.Condition <= 0))
            trail.WornOut = false;
        if (!wasRideable && trail.IsRideable)
            ctx.Publish(new TrailReopened(ctx.Tick, trail.Id));
    }

    /// <summary>
    /// Closes the trail when the crew is assigned to feature work on it, and opens it again when there is none
    /// (<see cref="RefreshUnderWork"/>); publishes <see cref="TrailClosed"/> / <see cref="TrailReopened"/>.
    /// </summary>
    public static void UpdateWorkClosure(SimContext ctx, Way trail)
    {
        var work = StartedFeatureWork(ctx.State, trail.Id);
        bool repairing = RefreshUnderWork(ctx.State, trail, ctx.Tick);
        if (repairing == trail.Repairing) return;
        bool wasRideable = trail.IsRideable;
        trail.Repairing = repairing;
        if (wasRideable && !trail.IsRideable)
            ctx.Publish(new TrailClosed(ctx.Tick, trail.Id, work?.Kind == JobKind.BuildFeature ? TrailClosedReason.Building : TrailClosedReason.Repair));
        else if (!wasRideable && trail.IsRideable)
            ctx.Publish(new TrailReopened(ctx.Tick, trail.Id));
    }

    /// <summary>True while any rider is travelling along the way (riding down a trail, or on it on the way to another).</summary>
    public static bool HasRidersOn(WorldState state, int wayId) =>
        state.Guests.Any(g => g.Activity is RiderActivity.Descending or RiderActivity.Climbing
                              && g.LegIndex >= 0 && g.LegIndex < g.Route.Count
                              && g.Route[g.LegIndex] is { Kind: LegKind.Way } leg && leg.WayId == wayId);

    /// <summary>
    /// Feature work the crew is assigned to but can't begin yet: the trail is closed, but riders who were already on
    /// it are still riding down. Nobody works on a trail with traffic.
    /// </summary>
    public static bool IsWaitingForRiders(WorldState state, Job job) =>
        job.Kind is JobKind.RepairFeature or JobKind.BuildFeature && HasRidersOn(state, job.WayId);

    /// <summary>
    /// Whether the trail is closed for feature work now: the crew has started on a job on it, or just finished one and
    /// the next job on the same trail is about to start (a 15 minute grace, so the trail doesn't open and close between
    /// two features).
    /// </summary>
    public static bool RefreshUnderWork(WorldState state, Way trail, long tick)
    {
        if (StartedFeatureWork(state, trail.Id) is not null)
        {
            trail.LastWorkTick = tick;
            return true;
        }
        return trail.LastWorkTick >= 0 && tick - trail.LastWorkTick <= 15
            && state.Jobs.Any(j => j.Kind is JobKind.RepairFeature or JobKind.BuildFeature && j.WayId == trail.Id && Systems.JobSystem.IsWorkable(state, j));
    }

    /// <summary>True once the crew has started on the job (work that hasn't started doesn't close the trail).</summary>
    public static bool IsStarted(WorldState state, Job job) =>
        job.Progress > 0 || state.Crew.Any(m => m.JobId == job.Id);

    private static void Release(WorldState state, Job job)
    {
        foreach (var member in state.Crew)
            if (member.JobId == job.Id)
                member.JobId = 0;
        state.Jobs.Remove(job);
    }
}
