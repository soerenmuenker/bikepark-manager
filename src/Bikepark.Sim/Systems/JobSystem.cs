using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>
/// The crew at work. During work hours, every minute:
/// <list type="number">
///   <item>workers whose job can't go on are freed; free workers (in hiring order) take the first workable job in the
///   queue that has room (<see cref="CrewRules.MaxWorkersPerJob"/>); a job needing wood takes it from the stock when
///   its first worker starts;</item>
///   <item>each job advances by its workers' speed (<see cref="WorkCosts.SpeedPermille"/> for the work it needs now):
///   first its trees are felled one by one (each adds wood), then the main work is done; a finished job builds its
///   way or feature.</item>
/// </list>
/// After the shift, workers stay on for up to <see cref="CrewRules.OvertimeMinutes"/> if their job can be finished in
/// the overtime left; nobody starts new work. Outside work hours everyone goes home. No randomness.
/// </summary>
public sealed class JobSystem : ISimSystem
{
    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        if (state.Crew.Count == 0)
            return;

        bool shift = ParkSchedule.IsCrewShift(state, ctx.Tick);
        bool overtime = ParkSchedule.IsCrewOvertime(state, ctx.Tick);
        if (!shift && !overtime || state.Jobs.Count == 0)
        {
            foreach (var member in state.Crew)
                member.JobId = 0;
            return;
        }

        if (shift)
            Assign(state);
        else
            KeepFinishableJobs(state, ParkSchedule.OvertimeLeft(state, ctx.Tick));

        // Work, in queue order (a finished job leaves the list, so iterate over a copy).
        foreach (var job in state.Jobs.ToList())
        {
            int workers = state.Crew.Count(m => m.JobId == job.Id);
            if (workers == 0) continue;
            state.CrewStats.CrewMinutesWorked += workers;
            job.Progress += (long)workers * WorkCosts.SpeedPermille(state, job.CurrentWorkType);
            Advance(ctx, job);
        }
    }

    /// <summary>True if the crew can work on the job now (wood the job has not taken yet must be in stock).</summary>
    public static bool IsWorkable(WorldState state, Job job) => job.Kind switch
    {
        JobKind.FellTrees => job.IsFelling,
        JobKind.BuildFeature => state.Ways.FirstOrDefault(w => w.Id == job.WayId)?.Built == true
                                && (job.WoodTaken || state.WoodStock >= job.Wood),
        _ => true,
    };

    private static void Assign(WorldState state)
    {
        foreach (var member in state.Crew)
            if (member.JobId != 0 && (Jobs.Find(state, member.JobId) is not { } current || !IsWorkable(state, current)))
                member.JobId = 0;

        foreach (var member in state.Crew)
        {
            if (member.JobId != 0) continue;
            foreach (var job in state.Jobs)
            {
                if (!IsWorkable(state, job)) continue;
                if (state.Crew.Count(m => m.JobId == job.Id) >= state.CrewRules.MaxWorkersPerJob) continue;
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

    /// <summary>Overtime: workers stay only on workable jobs their crew can finish in the minutes left.</summary>
    private static void KeepFinishableJobs(WorldState state, int minutesLeft)
    {
        foreach (var member in state.Crew)
        {
            if (member.JobId == 0) continue;
            if (Jobs.Find(state, member.JobId) is not { } job || !IsWorkable(state, job)) { member.JobId = 0; continue; }
            int workers = state.Crew.Count(m => m.JobId == job.Id);
            long canDo = (long)workers * WorkCosts.SpeedPermille(state, job.CurrentWorkType) * minutesLeft;
            if (WorkCosts.RemainingMinutes(state.CrewRules, job) * 1000 > canDo)
                foreach (var other in state.Crew.Where(m => m.JobId == job.Id))
                    other.JobId = 0;
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
