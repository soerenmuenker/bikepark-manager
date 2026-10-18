using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Trails;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Commands;

/// <summary>
/// Hires a worker, paid their wage at the end of every day. With <see cref="ApplicantId"/>: that applicant (their grade,
/// speed and wage; they leave the list). Without: a standard worker at <see cref="CrewRules.WagePerDayCents"/> and base
/// speed. No name: the applicant's, or "Worker n". There is no limit to the crew's size.
/// </summary>
public sealed record HireCrewCommand(string? Name = null, int ApplicantId = 0) : ICommand
{
    public const int MaxNameLength = 30;

    public string? Validate(SimContext ctx)
    {
        if (Name is { Length: > MaxNameLength }) return $"Name cannot exceed {MaxNameLength} characters.";
        if (ApplicantId != 0 && ctx.State.Applicants.All(a => a.Id != ApplicantId)) return "That applicant has found another job.";
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var applicant = state.Applicants.FirstOrDefault(a => a.Id == ApplicantId);
        string name = Name?.Trim() ?? "";
        if (name.Length == 0) name = applicant?.Name ?? "";
        if (name.Length == 0)
        {
            int n = 1;
            while (state.Crew.Any(m => m.Name == $"Worker {n}")) n++;
            name = $"Worker {n}";
        }
        var member = new CrewMember
        {
            Id = state.AllocateEntityId(),
            Name = name,
            GradeId = applicant?.GradeId ?? "",
            SpeedPermille = applicant?.SpeedPermille ?? WorkCosts.BaseSpeedPermille,
            WagePerDayCents = applicant?.WagePerDayCents ?? state.CrewRules.WagePerDayCents,
            HiredTick = ctx.Tick,
        };
        if (applicant is not null) state.Applicants.Remove(applicant);
        state.Crew.Add(member);
        ctx.Publish(new CrewHired(ctx.Tick, member.Id));
    }
}

/// <summary>
/// How many workers a job may have at most (0 = as many as the work allows, <see cref="Systems.JobSystem.AutoLimit"/>).
/// Fewer workers leave the rest free for the next jobs in the queue.
/// </summary>
public sealed record SetJobWorkersCommand(int JobId, int Workers) : ICommand
{
    public const int MaxWorkers = 1000;

    public string? Validate(SimContext ctx)
    {
        if (Jobs.Find(ctx.State, JobId) is null) return "No such job.";
        return Workers is < 0 or > MaxWorkers ? $"Set 0 (as many as possible) to {MaxWorkers} workers." : null;
    }

    public void Apply(SimContext ctx) => Jobs.Find(ctx.State, JobId)!.Workers = Workers;
}

/// <summary>Lets a worker go (no more wages from the end of this day); their job carries on with the others.</summary>
public sealed record DismissCrewCommand(int CrewId) : ICommand
{
    public string? Validate(SimContext ctx) => ctx.State.Crew.Any(m => m.Id == CrewId) ? null : "No such worker.";

    public void Apply(SimContext ctx)
    {
        ctx.State.Crew.RemoveAll(m => m.Id == CrewId);
        ctx.Publish(new CrewDismissed(ctx.Tick, CrewId));
    }
}

/// <summary>Buys one more tool: one more worker can work faster at its kind of work (until it is worn out).</summary>
public sealed record BuyToolCommand(string ToolId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        var state = ctx.State;
        if (state.ToolTypes.FirstOrDefault(t => t.Id == ToolId) is not { } tool) return $"Unknown tool '{ToolId}'.";
        int level = Reputation.ParkProgress.CurrentLevel(state, ctx.Network);
        if (state.Rules.UnlockLevel(level) < tool.RequiredLevel)
            return $"The {tool.Name.ToLowerInvariant()} needs park level {tool.RequiredLevel} (you are level {level}).";
        if (state.Finance.MoneyCents < tool.PriceCents) return "Not enough money.";
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        var tool = state.ToolTypes.First(t => t.Id == ToolId);
        state.Finance.Invest(MoneyCategory.Tools, tool.PriceCents, ctx.Tick, $"Bought a {tool.Name.ToLowerInvariant()}");
        state.Finance.TotalToolsCents += tool.PriceCents;
        state.Tools.Add(new OwnedTool { Id = state.AllocateEntityId(), TypeId = ToolId });
        ctx.Publish(new ToolBought(ctx.Tick, ToolId));
    }
}

/// <summary>Buys wood at <see cref="CrewRules.WoodPriceCents"/> per unit (felling is much cheaper).</summary>
public sealed record BuyWoodCommand(int Amount) : ICommand
{
    public const int MaxAmount = 1000;

    public string? Validate(SimContext ctx)
    {
        if (Amount is < 1 or > MaxAmount) return $"Buy between 1 and {MaxAmount} wood.";
        if (ctx.State.Finance.MoneyCents < Amount * ctx.State.CrewRules.WoodPriceCents) return "Not enough money.";
        return null;
    }

    public void Apply(SimContext ctx)
    {
        var state = ctx.State;
        long price = Amount * state.CrewRules.WoodPriceCents;
        state.Finance.Invest(MoneyCategory.Wood, price, ctx.Tick, $"Bought {Amount} wood");
        state.Finance.TotalWoodCents += price;
        state.WoodStock += Amount;
        state.CrewStats.WoodBought += Amount;
        ctx.Publish(new WoodBought(ctx.Tick, Amount));
    }
}

/// <summary>Queues felling the standing trees in a circle (validated by <see cref="ClearingPlanner"/>); each tree gives wood.</summary>
public sealed record FellTreesCommand(PointCm Center, int RadiusCm) : ICommand
{
    public string? Validate(SimContext ctx) => Plan(ctx).FirstError;

    public void Apply(SimContext ctx)
    {
        var plan = Plan(ctx);
        Jobs.Queue(ctx, new Job
        {
            Kind = JobKind.FellTrees,
            Center = Center,
            RadiusCm = RadiusCm,
            Trees = plan.Trees,
            MainWorkType = WorkType.Felling,
        });
    }

    private ClearingPlan Plan(SimContext ctx) => ClearingPlanner.Plan(ctx.Terrain, ctx.Network, ctx.State, Center, RadiusCm);
}

/// <summary>Moves a job to the front of the queue: free workers take it first.</summary>
public sealed record PrioritizeJobCommand(int JobId) : ICommand
{
    public string? Validate(SimContext ctx) => Jobs.Find(ctx.State, JobId) is null ? "No such job." : null;

    public void Apply(SimContext ctx)
    {
        var jobs = ctx.State.Jobs;
        var job = jobs.First(j => j.Id == JobId);
        jobs.Remove(job);
        jobs.Insert(0, job);
    }
}

/// <summary>
/// Cancels a job. Building a way or feature: the planned way or feature is removed too (like deleting it). Felling:
/// trees cut so far stay cut. Repairing a trail: its automatic maintenance is turned off too (or it would be queued
/// again at once).
/// </summary>
public sealed record CancelJobCommand(int JobId) : ICommand
{
    public string? Validate(SimContext ctx)
    {
        if (Jobs.Find(ctx.State, JobId) is not { } job) return "No such job.";
        return job.Kind switch
        {
            JobKind.BuildWay => new DeleteWayCommand(job.WayId).Validate(ctx),
            JobKind.BuildFeature => new RemoveTrailFeatureCommand(job.WayId, job.FeatureId).Validate(ctx),
            _ => null,
        };
    }

    public void Apply(SimContext ctx)
    {
        var job = Jobs.Find(ctx.State, JobId)!;
        switch (job.Kind)
        {
            case JobKind.BuildWay:
                new DeleteWayCommand(job.WayId).Apply(ctx);
                break;
            case JobKind.BuildFeature:
                new RemoveTrailFeatureCommand(job.WayId, job.FeatureId).Apply(ctx);
                break;
            case JobKind.BuildSign when ctx.State.Sponsorships.FirstOrDefault(s => s.Id == job.FeatureId) is { } deal:
                Systems.SponsorSystem.End(ctx, deal, "No sign: you cancelled it."); // the deal goes with its sign
                break;
            default:
                Jobs.Cancel(ctx, job);
                break;
        }
    }
}
