using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Crew;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>
/// The crew at work. During opening hours, every minute:
/// <list type="number">
///   <item>workers whose job can't go on are freed; free workers (in hiring order) take the first workable job in the
///   queue that has room (<see cref="CrewRules.MaxWorkersPerJob"/>); a job needing wood takes it from the stock when
///   its first worker starts; feature work closes its trail at once but only begins once the last rider has left it;</item>
///   <item>each job advances by its workers' speed: base speed, plus the bonus of the tool each one uses (the best free
///   tool for the work the job needs now, handed out in queue order; tools wear with every minute and are gone when worn out):
///   first its trees are felled one by one (each adds wood), then the main work is done; a finished job builds its
///   way or feature.</item>
/// </list>
/// Work hours are the park's opening hours; outside them everyone goes home. No randomness.
/// </summary>
public sealed class JobSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Crew.Count == 0)
            return;

        if (!ParkSchedule.IsOpen(state, ctx.Tick) || state.Jobs.Count == 0)
        {
            foreach (var member in state.Crew)
                member.JobId = 0;
            return;
        }

        Assign(state);

        // A trail closes as soon as the crew is assigned to work on it, so no new riders enter it.
        foreach (var job in state.Jobs)
            if (job.Kind is JobKind.RepairFeature or JobKind.BuildFeature && state.Ways.FirstOrDefault(w => w.Id == job.WayId) is { } trail)
                Jobs.UpdateWorkClosure(ctx, trail);

        // Work, in queue order (a finished job leaves the list, so iterate over a copy). Workers take the best free tool
        // for their job's kind of work in queue order: the first jobs get the best tools.
        var free = new Dictionary<WorkType, Queue<(OwnedTool Tool, ToolType Type)>>();
        var worn = new List<OwnedTool>();
        foreach (var job in state.Jobs.ToList())
        {
            int workers = state.Crew.Count(m => m.JobId == job.Id);
            if (workers == 0) continue;
            if (Jobs.IsWaitingForRiders(state, job)) continue; // the last riders are still on the trail
            state.CrewStats.CrewMinutesWorked += workers;
            var type = job.CurrentWorkType;
            if (!free.TryGetValue(type, out var tools))
                free[type] = tools = new Queue<(OwnedTool, ToolType)>(WorkCosts.ToolsFor(state, type));
            for (int i = 0; i < workers; i++)
            {
                int speed = WorkCosts.BaseSpeedPermille;
                if (tools.TryDequeue(out var tool))
                {
                    speed += tool.Type.SpeedBonusPermille;
                    tool.Tool.MinutesUsed++;
                    if (tool.Type.LifeMinutes > 0 && tool.Tool.MinutesUsed >= tool.Type.LifeMinutes) worn.Add(tool.Tool);
                }
                job.Progress += speed;
            }
            Advance(ctx, job);
        }
        foreach (var tool in worn)
        {
            state.Tools.Remove(tool);
            ctx.Publish(new ToolWornOut(ctx.Tick, tool.Id, tool.TypeId));
        }
    }

    /// <summary>True if the crew can work on the job now (wood the job has not taken yet must be in stock).</summary>
    public static bool IsWorkable(WorldState state, Job job) => job.Kind switch
    {
        JobKind.FellTrees => job.IsFelling,
        JobKind.RepairFeature => state.Ways.FirstOrDefault(w => w.Id == job.WayId)?.Features.Any(f => f.Id == job.FeatureId && f.Built) == true,
        JobKind.BuildFeature => state.Ways.FirstOrDefault(w => w.Id == job.WayId)?.Built == true
                                && (job.WoodTaken || state.WoodStock >= job.Wood),
        _ => true,
    };

    /// <summary>Workers a job can use now: felling takes one per tree still standing in it, other work <see cref="CrewRules.MaxWorkersPerJob"/>.</summary>
    public static int WorkerLimit(WorldState state, Job job) => job.IsFelling
        ? Math.Max(state.CrewRules.MaxWorkersPerJob, job.Trees.Count - job.TreesFelled)
        : job.Workers > 0 ? Math.Min(job.Workers, state.CrewRules.MaxWorkersPerJob) : state.CrewRules.MaxWorkersPerJob;

    private static void Assign(WorldState state)
    {
        foreach (var member in state.Crew)
            if (member.JobId != 0 && (Jobs.Find(state, member.JobId) is not { } current || !IsWorkable(state, current)))
                member.JobId = 0;

        // Felling is over for a job: its extra workers move on.
        foreach (var job in state.Jobs)
        {
            int limit = WorkerLimit(state, job);
            int on = 0;
            foreach (var member in state.Crew)
                if (member.JobId == job.Id && ++on > limit)
                    member.JobId = 0;
        }

        foreach (var member in state.Crew)
        {
            if (member.JobId != 0) continue;
            foreach (var job in state.Jobs)
            {
                if (!IsWorkable(state, job)) continue;
                if (state.Crew.Count(m => m.JobId == job.Id) >= WorkerLimit(state, job)) continue;
                if (!job.WoodTaken && job.Wood > 0)
                {
                    state.WoodStock -= job.Wood;
                    state.CrewStats.WoodUsed += job.Wood;
                }
                job.WoodTaken = true;
                member.JobId = job.Id;
                break;
            }
        }
    }

    private static void Advance(SimContext ctx, Job job)
    {
        var rules = ctx.State.CrewRules;
        long perTree = rules.FellMinutesPerTree * 1000L;
        while (job.IsFelling && job.Progress >= perTree)
        {
            job.Progress -= perTree;
            Jobs.FellNext(ctx, job);
        }
        if (job.IsFelling)
            return;
        if (job.Progress >= job.WorkMinutes * 1000L)
            Jobs.Complete(ctx, job);
    }
}
