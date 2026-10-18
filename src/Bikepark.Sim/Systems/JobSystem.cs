using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Crew;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>
/// The crew at work. During opening hours, every minute:
/// <list type="number">
///   <item>workers whose job can't go on are freed; free workers (in hiring order) take the first workable job in the
///   queue that has room (<see cref="WorkerLimit"/>: a way takes workers per section, built in parallel); a job needing wood takes it from the stock when
///   its first worker starts; feature work closes its trail at once but only begins once the last rider has left it;</item>
///   <item>each job advances by its workers' speed: base speed plus the bonus of the tool each one uses (the best free
///   tool for the work the job needs now, handed out in queue order: <see cref="WorkCosts.ToolAssignments"/>; tools wear
///   with every minute and are gone when worn out), scaled by the worker's own speed and the manager's supervision:
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

        // Work, in queue order (a finished job leaves the list). Who uses which tool is decided first, for all jobs at once
        // (the first jobs get the best tools); each worker adds their own speed.
        var uses = WorkCosts.ToolAssignments(state);
        var worn = new List<OwnedTool>();
        foreach (var job in state.Jobs.ToList())
        {
            int workers = 0;
            foreach (var use in uses)
            {
                if (use.Job != job) continue;
                workers++;
                use.Member.MinutesWorked++;
                job.Progress += WorkCosts.WorkerSpeedPermille(state, use.Member, use.Type);
                if (use.Tool is { } tool)
                {
                    tool.MinutesUsed++;
                    if (use.Type!.LifeMinutes > 0 && tool.MinutesUsed >= use.Type.LifeMinutes) worn.Add(tool);
                }
            }
            if (workers == 0) continue; // nobody on it, or the last riders are still on the trail
            state.CrewStats.CrewMinutesWorked += workers;
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
        JobKind.BuildSign => state.Sponsorships.Any(s => s.Id == job.FeatureId && s.Status == Sponsors.SponsorshipStatus.Active)
                             && (job.WoodTaken || state.WoodStock >= job.Wood),
        _ => true,
    };

    /// <summary>Workers a job can use now: <see cref="AutoLimit"/>, or fewer if the player set <see cref="Job.Workers"/>.</summary>
    public static int WorkerLimit(WorldState state, Job job)
    {
        int limit = AutoLimit(state, job);
        return job.Workers > 0 ? Math.Min(job.Workers, limit) : limit;
    }

    /// <summary>
    /// The most workers the work itself allows: felling one per tree still standing (at least
    /// <see cref="CrewRules.WorkersPerSection"/>), a way <see cref="CrewRules.WorkersPerSection"/> per section still to
    /// build (<see cref="WorkCosts.SectionsLeft"/>: sections are built in parallel), a feature, repair or sign
    /// <see cref="CrewRules.WorkersPerSection"/> (one spot).
    /// </summary>
    public static int AutoLimit(WorldState state, Job job)
    {
        int perSection = state.CrewRules.WorkersPerSection;
        if (job.IsFelling) return Math.Max(perSection, job.Trees.Count - job.TreesFelled);
        return perSection * WorkCosts.SectionsLeft(state, job);
    }

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
