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

    /// <summary>Hub (station plateau, parking lot) an end attaches to instead of a way; 0 = none.</summary>
    public int StartHubId { get; init; }
    public int EndHubId { get; init; }

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
    /// <param name="onOwnLand">The park's land (<see cref="Land.LandMath.OwnedPredicate"/>); null = no land limits.</param>
    public static WayPlan Plan(TerrainGrid grid, WayNetwork network, TrailRules rules, WayKind kind, IReadOnlyList<PointCm> input,
        Func<long, long, bool>? onOwnLand = null)
    {
        var issues = new List<WayIssue>();
        var points = input.ToList();

        if (points.Count < 2)
            return Fail(kind, points, "tooFewPoints", "Place at least two points.");
        if (points.Count > rules.MaxControlPoints)
            return Fail(kind, points, "tooManyPoints", $"At most {rules.MaxControlPoints} points.");
        if (points.Any(p => !grid.Contains(p.X, p.Z)))
            return Fail(kind, points, "outsideMap", "All points must be on the map.");
        if (kind == WayKind.Trail && !network.HasBase)
            return Fail(kind, points, "needsAccessPath", "Build a gravel access path or a lift first: riders need a way up.");

        // Orientation: access paths start at their low end, trails at their high end.
        int first = grid.HeightAt(points[0].X, points[0].Z), last = grid.HeightAt(points[^1].X, points[^1].Z);
        bool reversed = kind == WayKind.AccessPath ? first > last : first < last;
        if (reversed) points.Reverse();

        // Snap endpoints onto the existing network: hubs (onto the edge of their flat area) win over ways; a trail snaps
        // onto a loose trail end nearby (to continue it) before any other way.
        int snapRadius = rules.SnapRadiusMeters * 100;
        WayJoin? startJoin = null, endJoin = null;
        int startHub = 0, endHub = 0;
        if (network.HubAt(points[0].X, points[0].Z, snapRadius) is { } sh)
        {
            startHub = sh.Id;
            points[0] = sh.Pad.ClosestEdgePoint(points[0].X, points[0].Z);
        }
        else if (((kind == WayKind.Trail ? network.LooseTrailEnd(points[0].X, points[0].Z, snapRadius, end: true) : null)
                  ?? network.Nearest(points[0].X, points[0].Z, snapRadius)) is { } s)
        {
            startJoin = new WayJoin(s.WayId, s.DistanceCm);
            points[0] = s.Point;
        }
        if (network.HubAt(points[^1].X, points[^1].Z, snapRadius) is { } eh)
        {
            endHub = eh.Id;
            points[^1] = eh.Pad.ClosestEdgePoint(points[^1].X, points[^1].Z);
        }
        else if (((kind == WayKind.Trail ? network.LooseTrailEnd(points[^1].X, points[^1].Z, snapRadius, end: false) : null)
                  ?? network.Nearest(points[^1].X, points[^1].Z, snapRadius)) is { } e)
        {
            endJoin = new WayJoin(e.WayId, e.DistanceCm);
            points[^1] = e.Point;
        }
        if (startJoin is not null && endJoin is not null && startJoin.WayId == endJoin.WayId
            && Math.Abs(startJoin.DistanceCm - endJoin.DistanceCm) < 100)
            return Fail(kind, points, "sameJunction", "Both ends attach to the same spot.");
        if (startHub != 0 && startHub == endHub)
            return Fail(kind, points, "sameJunction", "Both ends attach to the same plateau.");

        if (kind == WayKind.Trail)
            AlignWithTrailEnds(network, points, ref startJoin, ref endJoin);

        var geometry = WayGeometry.Build(grid, kind, points, rules.SegmentLengthMeters * 100, rules.PathGradingMeters);

        if (onOwnLand is not null)
            for (int i = 0; i < geometry.SampleCount; i++)
                if (!onOwnLand(geometry.Xs[i], geometry.Zs[i]))
                {
                    issues.Add(Error("notYourLand", "Not your land: buy the parcel first (Land menu).", geometry.Distances[i], geometry.Distances[i]));
                    break;
                }

        long length = geometry.LengthCm;
        if (length > rules.MaxLengthMeters * 100L)
            issues.Add(Error("tooLong", $"Too long: at most {rules.MaxLengthMeters} m."));

        CheckGrades(kind, rules, geometry, issues);
        CheckConnections(kind, network, startJoin is not null || startHub != 0, endJoin is not null || endHub != 0, issues);
        if (kind == WayKind.Trail && geometry.EndHeightCm >= geometry.StartHeightCm)
            issues.Add(Error("noDrop", "A trail must end lower than it starts."));

        // Crossing another way without a junction: riders can collide there.
        foreach (var other in network.Ways)
            if (network.TryGetGeometry(other.Id, out var otherGeometry))
                foreach (var (cm, _) in Crossings.Find(geometry, otherGeometry))
                    issues.Add(new WayIssue(IssueSeverity.Warning, "crossing", $"Crosses {other.Label} at {cm / 100} m: riders can collide there.", cm, cm));

        foreach (var link in network.Links)
            if (link.Towed && network.FindHub(link.FromHubId)?.Pad is { } from && network.FindHub(link.ToHubId)?.Pad is { } to)
                foreach (var (cm, _) in Crossings.FindOnTrack(geometry, from, to))
                    issues.Add(new WayIssue(IssueSeverity.Warning, "crossing",
                        $"Crosses the {link.Name} track at {cm / 100} m: riders can collide with riders on the T-bar.", cm, cm));

        var (trees, rocks) = CountCleared(grid, network, rules, kind, geometry);
        if (trees + rocks > 0)
            issues.Add(Warning("clearing", $"Clears {trees} trees and {rocks} rocks along the way."));

        return new WayPlan
        {
            Kind = kind,
            Points = points,
            StartJoin = startJoin,
            EndJoin = endJoin,
            StartHubId = startHub,
            EndHubId = endHub,
            Geometry = geometry,
            Reversed = reversed,
            TreesToClear = trees,
            RocksToClear = rocks,
            Issues = issues,
        };
    }

    /// <summary>How far the guide point added at a joined trail end lies along that trail's direction.</summary>
    private const long GuideCm = 1_000;

    /// <summary>
    /// A trail that continues from another trail's end (or leads into another trail's start) joins it without a kink: the
    /// end snaps exactly onto the other trail's end and a guide point is added along that trail's direction, so the
    /// spline leaves (enters) in line with it. (Joined trails become one trail, see <see cref="WayEditing.OnBuilt"/>.)
    /// </summary>
    private static void AlignWithTrailEnds(WayNetwork network, List<PointCm> points, ref WayJoin? startJoin, ref WayJoin? endJoin)
    {
        if (startJoin is { } s && network.FindWay(s.WayId) is { Kind: WayKind.Trail } above && network.TryGetGeometry(above.Id, out var a)
            && s.DistanceCm >= a.LengthCm - WayEditing.EndToleranceCm)
        {
            startJoin = new WayJoin(above.Id, a.LengthCm);
            var end = a.PositionAt(a.LengthCm);
            var back = a.PositionAt(Math.Max(0, a.LengthCm - GuideCm));
            points[0] = new PointCm(end.X, end.Z);
            points.InsertRange(1, Guides(points[0], end.X - back.X, end.Z - back.Z, points[1]));
        }
        if (endJoin is { } e && network.FindWay(e.WayId) is { Kind: WayKind.Trail } below && network.TryGetGeometry(below.Id, out var b)
            && e.DistanceCm <= WayEditing.EndToleranceCm)
        {
            endJoin = new WayJoin(below.Id, 0);
            var start = b.PositionAt(0);
            var ahead = b.PositionAt(Math.Min(b.LengthCm, GuideCm));
            points[^1] = new PointCm(start.X, start.Z);
            var guides = Guides(points[^1], start.X - ahead.X, start.Z - ahead.Z, points[^2]);
            guides.Reverse();
            points.InsertRange(points.Count - 1, guides);
        }

        // Two points along the direction, at most GuideCm (and half the way to the neighbouring control point) from the
        // joint, nearest first: the spline runs straight out of the joint before it turns.
        static List<PointCm> Guides(PointCm joint, long dx, long dz, PointCm neighbour)
        {
            long dir = Terrain.FixedMath.ISqrt(dx * dx + dz * dz);
            long nx = neighbour.X - joint.X, nz = neighbour.Z - joint.Z;
            long reach = Math.Min(GuideCm, Terrain.FixedMath.ISqrt(nx * nx + nz * nz) / 2);
            if (dir == 0 || reach < 200) return [];
            return [At(reach / 2), At(reach)];
            PointCm At(long d) => new((int)(joint.X + dx * d / dir), (int)(joint.Z + dz * d / dir));
        }
    }

    /// <summary>
    /// Gradients per segment: errors only at the extremes, steep stretches as (merged) warnings. Runs of consecutive
    /// segments with the same finding become one issue covering the whole stretch, quoting its steepest score.
    /// </summary>
    private static void CheckGrades(WayKind kind, TrailRules rules, WayGeometry geometry, List<WayIssue> issues)
    {
        (string Code, IssueSeverity Severity, int Worst, long At, long To)? run = null;
        void Flush()
        {
            if (run is not { } r) return;
            issues.Add(new WayIssue(r.Severity, r.Code, GradeMessage(r.Code, r.Worst, rules), r.At, r.To));
            run = null;
        }

        foreach (var segment in geometry.Segments)
        {
            int g = segment.GradientTenths;
            (string Code, IssueSeverity Severity, int Value)? finding = kind == WayKind.AccessPath
                ? Math.Abs(g) > rules.PathMaxGradient ? ("pathTooSteep", IssueSeverity.Error, Math.Abs(g))
                : Math.Abs(g) > rules.PathSteepGradient ? ("pathSteep", IssueSeverity.Warning, Math.Abs(g))
                : null
                : -g > rules.TrailMaxDropGradient ? ("trailTooSteep", IssueSeverity.Error, -g)
                : -g > rules.TrailSteepDropGradient ? ("trailSteep", IssueSeverity.Warning, -g)
                : g > rules.TrailMaxClimbGradient ? ("trailUphill", IssueSeverity.Error, g)
                : g > rules.TrailSteepClimbGradient ? ("trailClimb", IssueSeverity.Warning, g)
                : null;

            if (finding is not { } f)
            {
                Flush();
                continue;
            }
            if (run is { } r && r.Code == f.Code && r.To == segment.StartCm)
                run = r with { Worst = Math.Max(r.Worst, f.Value), To = segment.EndCm };
            else
            {
                Flush();
                run = (f.Code, f.Severity, f.Value, segment.StartCm, segment.EndCm);
            }
        }
        Flush();
    }

    private static string GradeMessage(string code, int worst, TrailRules rules) => code switch
    {
        "pathTooSteep" => $"Too steep for a gravel path: {Gradient.Format(worst)} (limit {Gradient.Format(rules.PathMaxGradient)}). Try a switchback.",
        "pathSteep" => $"Steep gravel section ({Gradient.Format(worst)}): riders crawl up and tire faster. Switchbacks help.",
        "trailTooSteep" => $"Too steep to ride: {Gradient.Format(-worst)} (limit {Gradient.Format(-rules.TrailMaxDropGradient)}).",
        "trailSteep" => $"Very steep section ({Gradient.Format(-worst)}): only skilled riders will enjoy it.",
        "trailUphill" => $"Climbs too much for a trail: {Gradient.Format(worst)} (limit {Gradient.Format(rules.TrailMaxClimbGradient)}).",
        _ => $"Uphill section ({Gradient.Format(worst)}): riders have to pedal.",
    };

    private static void CheckConnections(WayKind kind, WayNetwork network, bool startConnected, bool endConnected, List<WayIssue> issues)
    {
        if (kind == WayKind.AccessPath)
        {
            if (!network.IsEmpty && !startConnected && !endConnected)
                issues.Add(Error("notConnected", "Connect at least one end to an existing path, trail or station."));
            return;
        }

        if (!startConnected)
            issues.Add(Error("startNotConnected", "The trail must start on a path, trail or plateau (snap the top end to it)."));
        if (!endConnected)
            issues.Add(Error("endNotConnected", "The trail must end on a path, trail or station (snap the bottom end to it)."));
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

}
