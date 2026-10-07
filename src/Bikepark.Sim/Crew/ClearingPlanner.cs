using Bikepark.Sim.State;
using Bikepark.Sim.Terrain;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Crew;

/// <summary>A planned felling area: the trees it would cut (in felling order), the work and wood, and the issues.</summary>
public sealed class ClearingPlan
{
    public required PointCm Center { get; init; }
    public required int RadiusCm { get; init; }
    public List<PointCm> Trees { get; init; } = [];
    public WorkEstimate Estimate { get; init; }
    public required List<WayIssue> Issues { get; init; }

    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
    public string? FirstError => Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message;
}

/// <summary>Validates a felling area. The fell-trees command and the in-game preview both use it.</summary>
public static class ClearingPlanner
{
    public static ClearingPlan Plan(TerrainGrid grid, WayNetwork network, WorldState state, PointCm center, int radiusCm)
    {
        var rules = state.CrewRules;
        if (!grid.Contains(center.X, center.Z))
            return Fail(center, radiusCm, "outsideMap", "The centre must be on the map.");
        if (radiusCm < rules.MinClearingRadiusMeters * 100)
            return Fail(center, radiusCm, "tooSmall", $"Make the area at least {rules.MinClearingRadiusMeters} m across (radius).");
        if (radiusCm > rules.MaxClearingRadiusMeters * 100)
            return Fail(center, radiusCm, "tooLarge", $"The radius can be at most {rules.MaxClearingRadiusMeters} m.");

        var trees = Forest.TreesInCircle(grid, network, Forest.Taken(state), center, radiusCm);
        var issues = new List<WayIssue>();
        if (trees.Count == 0)
            issues.Add(new WayIssue(IssueSeverity.Error, "noTrees", "There are no standing trees here (or they are already marked)."));
        return new ClearingPlan
        {
            Center = center,
            RadiusCm = radiusCm,
            Trees = trees,
            Estimate = WorkCosts.Felling(rules, trees.Count),
            Issues = issues,
        };
    }

    private static ClearingPlan Fail(PointCm center, int radiusCm, string code, string message) => new()
    {
        Center = center,
        RadiusCm = radiusCm,
        Issues = [new WayIssue(IssueSeverity.Error, code, message)],
    };
}
