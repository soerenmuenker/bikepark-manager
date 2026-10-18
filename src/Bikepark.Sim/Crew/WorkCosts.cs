using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Crew;

/// <summary>What a job will take: trees to fell first (each gives wood), then the main work. Minutes are crew-minutes at base speed.</summary>
/// <summary>A worker at work this minute, on a job, with a tool (or by hand: null).</summary>
public readonly record struct ToolUse(CrewMember Member, Job Job, OwnedTool? Tool, ToolType? Type);

public readonly record struct WorkEstimate(int Trees, long FellMinutes, long WorkMinutes, WorkType WorkType, int WoodNeeded, int WoodGained)
{
    public long TotalMinutes => FellMinutes + WorkMinutes;
}

/// <summary>
/// The single place that says how much work and wood something takes, and how fast the crew is. Used by the commands
/// that create jobs, by <see cref="Systems.JobSystem"/>, by the tool previews and by reports.
/// </summary>
public static class WorkCosts
{
    public const int BaseSpeedPermille = 1000;

    /// <summary>Digging a way: minutes per meter by kind, more on segments steeper than the "steep" gradient.</summary>
    public static long WayWorkMinutes(CrewRules crew, TrailRules trail, WayKind kind, WayGeometry geometry)
    {
        int perMeter = kind == WayKind.Trail ? crew.TrailWorkMinutesPerMeter : crew.PathWorkMinutesPerMeter;
        long weightedCm = 0;
        foreach (var segment in geometry.Segments)
            weightedCm += (segment.EndCm - segment.StartCm) * (1000 + (IsSteep(trail, kind, segment) ? crew.SteepExtraPermille : 0));
        return Math.Max(1, weightedCm * perMeter / 100_000);
    }

    public static WorkEstimate Way(CrewRules crew, TrailRules trail, WayKind kind, WayGeometry geometry, int trees) =>
        new(trees, FellMinutes(crew, trees), WayWorkMinutes(crew, trail, kind, geometry), WorkType.Digging, 0, trees * crew.WoodPerTree);

    /// <summary>Repairing a feature: <see cref="CrewRules.RepairWorkPermille"/> of its build work, scaled by its wear.</summary>
    public static long RepairMinutes(CrewRules crew, TrailFeatureType type, TrailFeature feature) =>
        Math.Max(1, (long)type.WorkMinutes * crew.RepairWorkPermille / 1000 * (TrailCondition.Perfect - feature.Condition) / TrailCondition.Perfect);

    public static WorkType RepairWorkType(TrailFeatureType type) => FeatureWorkType(type);

    public static WorkEstimate Feature(TrailFeatureType type) =>
        new(0, 0, type.WorkMinutes, FeatureWorkType(type), type.Wood, 0);

    public static WorkEstimate Felling(CrewRules crew, int trees) =>
        new(trees, FellMinutes(crew, trees), 0, WorkType.Felling, 0, trees * crew.WoodPerTree);

    public static long FellMinutes(CrewRules crew, int trees) => (long)trees * crew.FellMinutesPerTree;

    /// <summary>Dirt and stone features are dug, wood features built by carpentry.</summary>
    public static WorkType FeatureWorkType(TrailFeatureType type) =>
        type.Material == FeatureMaterial.Wood ? WorkType.Carpentry : WorkType.Digging;

    /// <summary>Work one worker does per minute, in permille crew-minutes: base speed plus the best owned tool for it.</summary>
    public static int SpeedPermille(WorldState state, WorkType type)
    {
        var tools = ToolsFor(state, type);
        return BaseSpeedPermille + (tools.Count > 0 ? tools[0].Type.SpeedBonusPermille : 0);
    }

    /// <summary>The park's tools for one kind of work, best first (then oldest first: lower id).</summary>
    public static List<(OwnedTool Tool, ToolType Type)> ToolsFor(WorldState state, WorkType type)
    {
        var tools = new List<(OwnedTool, ToolType)>();
        foreach (var tool in state.Tools)
            if (state.ToolTypes.FirstOrDefault(t => t.Id == tool.TypeId) is { } toolType && toolType.WorkType == type)
                tools.Add((tool, toolType));
        return tools.OrderByDescending(t => t.Item2.SpeedBonusPermille).ThenBy(t => t.Item1.Id).ToList();
    }

    /// <summary>
    /// Work <paramref name="workers"/> workers do together per minute on one kind of work, in permille crew-minutes,
    /// if they get the best tools there are (for estimates; at work the tools are shared out over all jobs).
    /// </summary>
    public static long CrewSpeedPermille(WorldState state, WorkType type, int workers)
    {
        var tools = ToolsFor(state, type);
        long speed = 0;
        for (int i = 0; i < workers; i++)
            speed += BaseSpeedPermille + (i < tools.Count ? tools[i].Type.SpeedBonusPermille : 0);
        return speed;
    }

    /// <summary>
    /// Work one worker does per minute, in permille crew-minutes: base speed plus their tool's bonus, scaled by the
    /// worker's own speed and the park manager's supervision (<see cref="Staff.StaffMath.CrewSupervisionPermille"/>).
    /// </summary>
    public static int WorkerSpeedPermille(WorldState state, CrewMember member, ToolType? tool) =>
        (int)((long)(BaseSpeedPermille + (tool?.SpeedBonusPermille ?? 0)) * member.SpeedPermille / 1000
              * Staff.StaffMath.CrewSupervisionPermille(state) / 1000);

    /// <summary>
    /// Which worker uses which tool this minute: jobs in queue order (jobs waiting for riders left out), their workers in
    /// hiring order, each taking the best free tool of the job's kind of work. Derived, never saved; <see cref="Systems.JobSystem"/>
    /// works with exactly this, and the menus show it.
    /// </summary>
    public static List<ToolUse> ToolAssignments(WorldState state)
    {
        var uses = new List<ToolUse>();
        var free = new Dictionary<WorkType, Queue<(OwnedTool Tool, ToolType Type)>>();
        foreach (var job in state.Jobs)
        {
            if (Jobs.IsWaitingForRiders(state, job)) continue;
            var type = job.CurrentWorkType;
            foreach (var member in state.Crew)
            {
                if (member.JobId != job.Id) continue;
                if (!free.TryGetValue(type, out var tools))
                    free[type] = tools = new Queue<(OwnedTool, ToolType)>(ToolsFor(state, type));
                uses.Add(tools.TryDequeue(out var tool) ? new ToolUse(member, job, tool.Tool, tool.Type) : new ToolUse(member, job, null, null));
            }
        }
        return uses;
    }

    /// <summary>
    /// Sections of a way job still to build (each is <see cref="CrewRules.SectionMeters"/> of digging; at least 1), from
    /// its remaining main work. Other jobs are one spot (1).
    /// </summary>
    public static int SectionsLeft(WorldState state, Job job)
    {
        if (job.Kind != JobKind.BuildWay) return 1;
        var crew = state.CrewRules;
        var kind = state.Ways.FirstOrDefault(w => w.Id == job.WayId)?.Kind ?? WayKind.Trail;
        long perSection = Math.Max(1L, (long)(kind == WayKind.Trail ? crew.TrailWorkMinutesPerMeter : crew.PathWorkMinutesPerMeter) * crew.SectionMeters);
        long remaining = job.IsFelling ? job.WorkMinutes : Math.Max(0, job.WorkMinutes - job.Progress / 1000);
        return (int)Math.Clamp((remaining + perSection - 1) / perSection, 1, 10_000);
    }

    /// <summary>How worn a tool is: 1000 = new, 0 = worn out (always 1000 for tools that last forever).</summary>
    public static int ConditionPermille(ToolType type, OwnedTool tool) =>
        type.LifeMinutes <= 0 ? 1000 : (int)Math.Clamp(1000 - tool.MinutesUsed * 1000 / type.LifeMinutes, 0, 1000);

    /// <summary>All of a job's work in crew-minutes at base speed (felling plus main work).</summary>
    public static long TotalMinutes(CrewRules crew, Job job) => FellMinutes(crew, job.Trees.Count) + job.WorkMinutes;

    /// <summary>Work done so far, in crew-minutes at base speed.</summary>
    public static long DoneMinutes(CrewRules crew, Job job) => job.IsFelling
        ? FellMinutes(crew, job.TreesFelled) + job.Progress / 1000
        : FellMinutes(crew, job.Trees.Count) + job.Progress / 1000;

    /// <summary>0..1000.</summary>
    public static int ProgressPermille(CrewRules crew, Job job)
    {
        long total = TotalMinutes(crew, job);
        return total <= 0 ? 1000 : (int)Math.Clamp(DoneMinutes(crew, job) * 1000 / total, 0, 1000);
    }

    /// <summary>Crew-minutes the job will still take at base speed.</summary>
    public static long RemainingMinutes(CrewRules crew, Job job) => Math.Max(0, TotalMinutes(crew, job) - DoneMinutes(crew, job));

    private static bool IsSteep(TrailRules rules, WayKind kind, WaySegment segment)
    {
        int g = segment.GradientTenths;
        return kind == WayKind.AccessPath
            ? Math.Abs(g) > rules.PathSteepGradient
            : g < -rules.TrailSteepDropGradient || g > rules.TrailSteepClimbGradient;
    }
}
