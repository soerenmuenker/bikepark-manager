using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Crew;

/// <summary>What a job will take: trees to fell first (each gives wood), then the main work. Minutes are crew-minutes at base speed.</summary>
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

    /// <summary>Repairing a trail: <see cref="CrewRules.RepairMinutesPerSegment"/> per segment, scaled by its wear.</summary>
    public static long RepairMinutes(CrewRules crew, Way way, int segments)
    {
        long missing = 0;
        for (int i = 0; i < segments; i++)
            missing += TrailCondition.Perfect - TrailCondition.Get(way, i);
        return Math.Max(1, missing * crew.RepairMinutesPerSegment / TrailCondition.Perfect);
    }

    public static WorkEstimate Feature(TrailFeatureType type) =>
        new(0, 0, type.WorkMinutes, FeatureWorkType(type), type.Wood, 0);

    public static WorkEstimate Felling(CrewRules crew, int trees) =>
        new(trees, FellMinutes(crew, trees), 0, WorkType.Felling, 0, trees * crew.WoodPerTree);

    public static long FellMinutes(CrewRules crew, int trees) => (long)trees * crew.FellMinutesPerTree;

    /// <summary>Dirt features are dug, wood features built by carpentry.</summary>
    public static WorkType FeatureWorkType(TrailFeatureType type) =>
        type.Material == FeatureMaterial.Wood ? WorkType.Carpentry : WorkType.Digging;

    /// <summary>Work one worker does per minute, in permille crew-minutes: base speed plus the best owned tool for it.</summary>
    public static int SpeedPermille(WorldState state, WorkType type)
    {
        int bonus = 0;
        foreach (string id in state.OwnedToolIds)
            if (state.ToolTypes.FirstOrDefault(t => t.Id == id) is { } tool && tool.WorkType == type)
                bonus = Math.Max(bonus, tool.SpeedBonusPermille);
        return BaseSpeedPermille + bonus;
    }

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
