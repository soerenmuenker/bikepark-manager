using Bikepark.Sim.Terrain;

namespace Bikepark.Sim.Trails;

public enum IssueSeverity : byte
{
    Warning = 0,
    Error = 1,
}

/// <summary>A problem with a planned way. AtCm/ToCm locate it along the planned way (-1 = whole way).</summary>
public sealed record WayIssue(IssueSeverity Severity, string Code, string Message, long AtCm = -1, long ToCm = -1);

/// <summary>The result of planning a way: oriented and snapped points, geometry, stats and issues.</summary>
public sealed class WayPlan
{
    public required WayKind Kind { get; init; }

    /// <summary>Control points as they will be built: oriented, endpoints moved onto their junctions.</summary>
    public required List<PointCm> Points { get; init; }

    public WayJoin? StartJoin { get; init; }
    public WayJoin? EndJoin { get; init; }
    public WayGeometry? Geometry { get; init; }

    /// <summary>True if the input was drawn the other way round and has been flipped.</summary>
    public bool Reversed { get; init; }

    public int TreesToClear { get; init; }
    public int RocksToClear { get; init; }
    public required List<WayIssue> Issues { get; init; }

    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);
    public long LengthCm => Geometry?.LengthCm ?? 0;
    public long DropCm => Geometry is null ? 0 : Geometry.StartHeightCm - Geometry.EndHeightCm;

    public string? FirstError => Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message;
}

/// <summary>
/// Validates a drawn way against the terrain, the existing network and the rules. Pure and read-only: the build
/// command uses it, and the view calls it every frame for the live preview.
/// </summary>
public static class WayPlanner
{
    public static WayPlan Plan(TerrainGrid grid, WayNetwork network, TrailRules rules, WayKind kind, IReadOnlyList<PointCm> input)
    {
        var issues = new List<WayIssue>();
        var points = input.ToList();

        if (points.Count < 2)
            return Fail(kind, points, "tooFewPoints", "Place at least two points.");
        if (points.Count > rules.MaxControlPoints)
            return Fail(kind, points, "tooManyPoints", $"At most {rules.MaxControlPoints} points.");
        if (points.Any(p => !grid.Contains(p.X, p.Z)))
            return Fail(kind, points, "outsideMap", "All points must be on the map.");
        if (kind == WayKind.Trail && !network.HasAccessPath)
            return Fail(kind, points, "needsAccessPath", "Build a gravel access path first: riders need it to get up.");

        // Orientation: access paths start at their low end, trails at their high end.
        int first = grid.HeightAt(points[0].X, points[0].Z), last = grid.HeightAt(points[^1].X, points[^1].Z);
        bool reversed = kind == WayKind.AccessPath ? first > last : first < last;
        if (reversed) points.Reverse();

        // Snap endpoints onto the existing network.
        int snapRadius = rules.SnapRadiusMeters * 100;
        WayJoin? startJoin = null, endJoin = null;
        if (network.Nearest(points[0].X, points[0].Z, snapRadius) is { } s)
        {
            startJoin = new WayJoin(s.WayId, s.DistanceCm);
            points[0] = s.Point;
        }
        if (network.Nearest(points[^1].X, points[^1].Z, snapRadius) is { } e)
        {
            endJoin = new WayJoin(e.WayId, e.DistanceCm);
            points[^1] = e.Point;
        }
        if (startJoin is not null && endJoin is not null && startJoin.WayId == endJoin.WayId
            && Math.Abs(startJoin.DistanceCm - endJoin.DistanceCm) < 100)
            return Fail(kind, points, "sameJunction", "Both ends attach to the same spot.");

        var geometry = WayGeometry.Build(grid, kind, points, rules.SegmentLengthMeters * 100);

        long length = geometry.LengthCm;
        if (length < rules.MinLengthMeters * 100L)
            issues.Add(Error("tooShort", $"Too short: at least {rules.MinLengthMeters} m."));
        if (length > rules.MaxLengthMeters * 100L)
            issues.Add(Error("tooLong", $"Too long: at most {rules.MaxLengthMeters} m."));

        CheckGrades(kind, rules, geometry, issues);
        CheckConnections(kind, network, startJoin, endJoin, issues);
        if (kind == WayKind.Trail && geometry.EndHeightCm >= geometry.StartHeightCm)
            issues.Add(Error("noDrop", "A trail must end lower than it starts."));

        var (trees, rocks) = CountCleared(grid, network, rules, kind, geometry);
        if (trees + rocks > 0)
            issues.Add(Warning("clearing", $"Clears {trees} trees and {rocks} rocks along the way."));

        return new WayPlan
        {
            Kind = kind,
            Points = points,
            StartJoin = startJoin,
            EndJoin = endJoin,
            Geometry = geometry,
            Reversed = reversed,
            TreesToClear = trees,
            RocksToClear = rocks,
            Issues = issues,
        };
    }

    private static void CheckGrades(WayKind kind, TrailRules rules, WayGeometry geometry, List<WayIssue> issues)
    {
        foreach (var segment in geometry.Segments)
        {
            int grade = segment.GradePermille;
            if (kind == WayKind.AccessPath && Math.Abs(grade) > rules.PathMaxGradePermille)
                issues.Add(Error("pathTooSteep",
                    $"Too steep for a gravel path: {Percent(Math.Abs(grade))} (max {Percent(rules.PathMaxGradePermille)}).",
                    segment.StartCm, segment.EndCm));
            else if (kind == WayKind.Trail && -grade > rules.TrailMaxDownGradePermille)
                issues.Add(Error("trailTooSteep",
                    $"Too steep: {Percent(-grade)} downhill (max {Percent(rules.TrailMaxDownGradePermille)}).",
                    segment.StartCm, segment.EndCm));
            else if (kind == WayKind.Trail && grade > rules.TrailMaxUpGradePermille)
                issues.Add(Error("trailUphill",
                    $"Climbs {Percent(grade)} (max {Percent(rules.TrailMaxUpGradePermille)} uphill on a trail).",
                    segment.StartCm, segment.EndCm));
        }
    }

    private static void CheckConnections(WayKind kind, WayNetwork network, WayJoin? start, WayJoin? end, List<WayIssue> issues)
    {
        if (kind == WayKind.AccessPath)
        {
            if (network.Ways.Count > 0 && start is null && end is null)
                issues.Add(Error("notConnected", "Connect at least one end to an existing path or trail."));
            return;
        }

        if (start is null)
            issues.Add(Error("startNotConnected", "The trail must start on a path or trail (snap the top end to it)."));
        if (end is null)
            issues.Add(Error("endNotConnected", "The trail must end on a path or trail (snap the bottom end to it)."));
    }

    private static (int Trees, int Rocks) CountCleared(
        TerrainGrid grid, WayNetwork network, TrailRules rules, WayKind kind, WayGeometry geometry)
    {
        var corridor = new CorridorIndex();
        int width = kind == WayKind.AccessPath ? rules.PathCorridorCm : rules.TrailCorridorCm;
        corridor.Add(0, geometry, width);

        int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
        for (int i = 0; i < geometry.SampleCount; i++)
        {
            minX = Math.Min(minX, geometry.Xs[i]);
            maxX = Math.Max(maxX, geometry.Xs[i]);
            minZ = Math.Min(minZ, geometry.Zs[i]);
            maxZ = Math.Max(maxZ, geometry.Zs[i]);
        }
        int margin = width / 2 + TerrainScatter.RockCellMeters * 100;
        var scatter = new List<ScatterInstance>();
        TerrainScatter.Collect(grid,
            Math.Max(0, (minX - margin) / 100), Math.Max(0, (minZ - margin) / 100),
            (maxX + margin) / 100 + 1, (maxZ + margin) / 100 + 1, scatter);

        int trees = 0, rocks = 0;
        foreach (var item in scatter)
        {
            if (!corridor.Contains(item.XCm, item.ZCm) || network.IsInCorridor(item.XCm, item.ZCm)) continue;
            if (item.Kind == ScatterKind.Tree) trees++; else rocks++;
        }
        return (trees, rocks);
    }

    private static WayPlan Fail(WayKind kind, List<PointCm> points, string code, string message) => new()
    {
        Kind = kind,
        Points = points,
        Issues = [Error(code, message)],
    };

    private static WayIssue Error(string code, string message, long at = -1, long to = -1) =>
        new(IssueSeverity.Error, code, message, at, to);

    private static WayIssue Warning(string code, string message) => new(IssueSeverity.Warning, code, message);

    private static string Percent(int permille) => $"{permille / 10} %";
}
