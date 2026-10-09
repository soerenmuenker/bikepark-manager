using Bikepark.Sim.Events;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Trails;

/// <summary>
/// Editing built trails: splitting one into two, renaturalizing a section (the trail is gone there, trees grow back),
/// joining a newly built trail onto loose trail ends (it becomes part of them), and splitting a trail where a new gravel
/// path attaches (gravel routes divide trails). Only stored input changes: control points (re-sampled from the derived
/// geometry), joins, features and closures; everything else is derived again. Pieces shorter than the minimum way length
/// are dropped.
/// </summary>
public static class WayEditing
{
    /// <summary>How close to a way's end a join counts as "at the end".</summary>
    public const long EndToleranceCm = 150;

    /// <summary>Spacing of the control points re-sampled from a trail's geometry.</summary>
    private const long SampleCm = 1_000;

    /// <summary>Why a trail can't be split or renaturalized now, or null.</summary>
    public static string? CannotEdit(WorldState state, Way? way)
    {
        if (way is null) return "No such trail.";
        if (way.Kind != WayKind.Trail) return $"{way.Name} is a gravel path: only trails can be split or renaturalized.";
        if (!way.Built) return $"{way.Name} is only planned: cancel its job instead.";
        if (way.Origin == WayOrigin.Scenario) return $"{way.Name} belongs to the scenario.";
        if (state.Jobs.Any(j => j.WayId == way.Id)) return $"The crew is working on {way.Name}: wait or cancel the job first.";
        return null;
    }

    /// <summary>Why the trail can't be split at this distance, or null.</summary>
    public static string? CannotSplit(WorldState state, WayNetwork network, Way? way, long atCm)
    {
        if (CannotEdit(state, way) is { } reason) return reason;
        long length = network.Geometry(way!.Id).LengthCm;
        long min = MinPieceCm(state);
        if (atCm < min || atCm > length - min) return $"Both parts must be at least {min / 100} m long.";
        if (network.FeaturesOn(way.Id).Any(f => f.StartCm < atCm && atCm < f.EndCm)) return "Not on a feature: split before or after it.";
        return null;
    }

    /// <summary>Why the section can't be renaturalized, or null.</summary>
    public static string? CannotRenaturalize(WorldState state, WayNetwork network, Way? way, long fromCm, long toCm)
    {
        if (CannotEdit(state, way) is { } reason) return reason;
        long length = network.Geometry(way!.Id).LengthCm;
        if (fromCm < 0 || toCm > length || toCm - fromCm < 100) return "Mark a section along the trail.";
        return null;
    }

    // ---------------------------------------------------------------- split

    /// <summary>Splits a trail at a distance: the upper part keeps the trail (name, stats), the lower one is a new trail.</summary>
    public static Way Split(SimContext ctx, Way way, long atCm)
    {
        var state = ctx.State;
        var geometry = ctx.Network.Geometry(way.Id);
        long length = geometry.LengthCm;
        var upper = Resample(state, geometry, 0, atCm);
        var lower = Resample(state, geometry, atCm, length);
        long upperLength = Measure(ctx, way.Kind, upper);

        var second = NewPiece(state, way, lower, NextName(state, way.Name));
        second.StartJoin = new WayJoin(way.Id, upperLength); // the lower part starts where the upper one ends
        second.EndJoin = way.EndJoin;
        second.EndHubId = way.EndHubId;
        way.Points = upper;
        way.EndJoin = null;
        way.EndHubId = 0;
        MoveFeatures(way, second, atCm, atCm);
        RemapJoins(state, way.Id, d => d <= atCm ? (way.Id, Math.Min(d, upperLength)) : (second.Id, d - atCm));
        Finish(ctx, way, second);
        ctx.Publish(new TrailSplit(ctx.Tick, way.Id, second.Id));
        return second;
    }

    // ---------------------------------------------------------------- renaturalize

    /// <summary>
    /// Removes a section of a trail. What is left above keeps the trail; what is left below becomes a new trail; both have
    /// a loose end there (closed until reconnected). Remnants shorter than the minimum way length go too. Ways that joined
    /// the removed section get a loose end.
    /// </summary>
    public static void Renaturalize(SimContext ctx, Way way, long fromCm, long toCm)
    {
        var state = ctx.State;
        var geometry = ctx.Network.Geometry(way.Id);
        long length = geometry.LengthCm;
        long min = MinPieceCm(state);
        bool keepUpper = fromCm >= min, keepLower = toCm <= length - min;
        int id = way.Id;

        if (!keepUpper && !keepLower)
        {
            RemapJoins(state, id, _ => null);
            state.Ways.Remove(way);
            state.WaysRevision++;
            Systems.RiderSystem.ResetRidersUsing(ctx, id);
            ctx.Publish(new TrailRenaturalized(ctx.Tick, id, 0, fromCm, toCm));
            ctx.Publish(new WayDeleted(ctx.Tick, id));
            return;
        }

        Way? lower = null;
        if (keepUpper && keepLower)
        {
            lower = NewPiece(state, way, Resample(state, geometry, toCm, length), NextName(state, way.Name));
            lower.EndJoin = way.EndJoin;
            lower.EndHubId = way.EndHubId;
            way.Points = Resample(state, geometry, 0, fromCm);
            way.EndJoin = null;
            way.EndHubId = 0;
            MoveFeatures(way, lower, fromCm, toCm);
            RemapJoins(state, id, d => d < fromCm ? (id, d) : d > toCm ? (lower.Id, d - toCm) : null);
        }
        else if (keepUpper)
        {
            way.Points = Resample(state, geometry, 0, fromCm);
            way.EndJoin = null;
            way.EndHubId = 0;
            way.Features.RemoveAll(f => f.DistanceCm + FeatureLength(state, f) > fromCm);
            RemapJoins(state, id, d => d < fromCm ? (id, d) : null);
        }
        else
        {
            // Only the lower part is left: it keeps the trail, starting loose.
            way.Points = Resample(state, geometry, toCm, length);
            way.StartJoin = null;
            way.StartHubId = 0;
            way.Features.RemoveAll(f => f.DistanceCm < toCm);
            foreach (var feature in way.Features) feature.DistanceCm -= toCm;
            RemapJoins(state, id, d => d > toCm ? (id, d - toCm) : null);
        }
        Finish(ctx, way, lower);
        ctx.Publish(new TrailRenaturalized(ctx.Tick, id, lower?.Id ?? 0, fromCm, toCm));
    }

    // ---------------------------------------------------------------- when a way is built

    /// <summary>
    /// A newly built trail that starts on a trail's loose end becomes part of that trail, and a trail that ends on a
    /// trail's loose start takes that trail in (so a gap can be closed into one trail again). A newly built gravel path
    /// that ends in the middle of a trail splits the trail there: gravel routes divide trails.
    /// </summary>
    public static void OnBuilt(SimContext ctx, Way way)
    {
        var state = ctx.State;
        if (way.Kind == WayKind.AccessPath)
        {
            foreach (var join in new[] { way.StartJoin, way.EndJoin })
            {
                if (join is null || state.Ways.FirstOrDefault(w => w.Id == join.WayId) is not { Kind: WayKind.Trail } trail) continue;
                if (CannotSplit(state, ctx.Network, trail, join.DistanceCm) is null)
                    Split(ctx, trail, join.DistanceCm);
            }
            return;
        }

        var target = way;
        if (way.StartJoin is { } start && state.Ways.FirstOrDefault(w => w.Id == start.WayId) is { Kind: WayKind.Trail, Built: true } above
            && above != way && start.DistanceCm >= ctx.Network.Geometry(above.Id).LengthCm - EndToleranceCm && IsLooseEnd(ctx, above, way))
        {
            Absorb(ctx, above, way);
            target = above;
        }
        if (target.EndJoin is { } end && state.Ways.FirstOrDefault(w => w.Id == end.WayId) is { Kind: WayKind.Trail, Built: true } below
            && below != target && end.DistanceCm <= EndToleranceCm && IsLooseStart(state, below, target) && CannotEdit(state, below) is null)
            Absorb(ctx, target, below);
    }

    /// <summary>Nothing continues from the trail's end except <paramref name="except"/>.</summary>
    private static bool IsLooseEnd(SimContext ctx, Way trail, Way except)
    {
        long length = ctx.Network.Geometry(trail.Id).LengthCm;
        return trail.EndJoin is null && trail.EndHubId == 0
               && !ctx.State.Ways.Any(w => w != except && w != trail && Joins(w, trail.Id, d => d >= length - EndToleranceCm));
    }

    /// <summary>Nothing leads into the trail's start except <paramref name="except"/>.</summary>
    private static bool IsLooseStart(WorldState state, Way trail, Way except) =>
        trail.StartJoin is null && trail.StartHubId == 0
        && !state.Ways.Any(w => w != except && w != trail && Joins(w, trail.Id, d => d <= EndToleranceCm));

    private static bool Joins(Way w, int wayId, Func<long, bool> at) =>
        w.StartJoin is { } s && s.WayId == wayId && at(s.DistanceCm) || w.EndJoin is { } e && e.WayId == wayId && at(e.DistanceCm);

    /// <summary>Appends <paramref name="next"/> to <paramref name="trail"/> (its features, end and the ways joining it move over).</summary>
    private static void Absorb(SimContext ctx, Way trail, Way next)
    {
        var state = ctx.State;
        long offset = ctx.Network.Geometry(trail.Id).LengthCm;
        var points = new List<PointCm>(trail.Points);
        points.AddRange(next.Points.SkipWhile((p, i) => i == 0 && p == trail.Points[^1]));
        trail.Points = Thin(points, state.TrailRules.MaxControlPoints);
        trail.EndJoin = next.EndJoin;
        trail.EndHubId = next.EndHubId;
        foreach (var feature in next.Features)
        {
            feature.DistanceCm += offset;
            trail.Features.Add(feature);
        }
        trail.Features.Sort((a, b) => a.DistanceCm.CompareTo(b.DistanceCm));
        RemapJoins(state, next.Id, d => (trail.Id, d + offset));
        state.Ways.Remove(next);
        state.WaysRevision++;
        Systems.RiderSystem.ResetRidersUsing(ctx, trail.Id, next.Id);
        ctx.Publish(new TrailsJoined(ctx.Tick, trail.Id, next.Id));
        ctx.Publish(new WayDeleted(ctx.Tick, next.Id));
    }

    // ---------------------------------------------------------------- helpers

    private static long MinPieceCm(WorldState state) => state.TrailRules.MinLengthMeters * 100L;

    /// <summary>A new trail piece next to <paramref name="original"/> (same kind, origin and closure; fresh stats).</summary>
    private static Way NewPiece(WorldState state, Way original, List<PointCm> points, string name)
    {
        var piece = new Way
        {
            Id = state.AllocateEntityId(),
            Kind = original.Kind,
            Name = name,
            Points = points,
            Origin = original.Origin,
            Built = true,
            Closed = original.Closed,
        };
        state.Ways.Insert(state.Ways.IndexOf(original) + 1, piece);
        return piece;
    }

    /// <summary>Features from <paramref name="toCm"/> on move to the lower piece; those in the removed section go.</summary>
    private static void MoveFeatures(Way upper, Way lower, long fromCm, long toCm)
    {
        foreach (var feature in upper.Features.Where(f => f.DistanceCm >= toCm).ToList())
        {
            upper.Features.Remove(feature);
            feature.DistanceCm -= toCm;
            lower.Features.Add(feature);
        }
        upper.Features.RemoveAll(f => f.DistanceCm >= fromCm);
    }

    private static long FeatureLength(WorldState state, TrailFeature feature) =>
        TrailFeatures.FindType(state.TrailFeatureTypes, feature.TypeId)?.LengthCm ?? 0;

    /// <summary>Re-points every join onto <paramref name="wayId"/> (null: the joining way gets a loose end there).</summary>
    private static void RemapJoins(WorldState state, int wayId, Func<long, (int WayId, long DistanceCm)?> map)
    {
        foreach (var w in state.Ways)
        {
            if (w.StartJoin is { } s && s.WayId == wayId)
                w.StartJoin = map(s.DistanceCm) is { } to ? new WayJoin(to.WayId, to.DistanceCm) : null;
            if (w.EndJoin is { } e && e.WayId == wayId)
                w.EndJoin = map(e.DistanceCm) is { } to ? new WayJoin(to.WayId, to.DistanceCm) : null;
        }
    }

    private static void Finish(SimContext ctx, Way way, Way? other)
    {
        var state = ctx.State;
        foreach (var w in new[] { way, other })
            if (w is not null)
                w.WornOut = w.Features.Any(f => f.Built && TrailCondition.Permille(f) == 0);
        state.WaysRevision++;
        if (other is null) Systems.RiderSystem.ResetRidersUsing(ctx, way.Id);
        else Systems.RiderSystem.ResetRidersUsing(ctx, way.Id, other.Id);
    }

    /// <summary>Control points every ~10 m along the geometry from one distance to another (both ends included).</summary>
    internal static List<PointCm> Resample(WorldState state, WayGeometry geometry, long fromCm, long toCm)
    {
        int max = Math.Max(2, state.TrailRules.MaxControlPoints);
        long step = Math.Max(SampleCm, (toCm - fromCm + max - 2) / (max - 1));
        var points = new List<PointCm>();
        for (long d = fromCm; d < toCm; d += step)
            Add(points, geometry.PositionAt(d));
        Add(points, geometry.PositionAt(toCm));
        if (points.Count == 1) Add(points, geometry.PositionAt(Math.Max(fromCm, toCm - 1)));
        return points;

        static void Add(List<PointCm> list, WayPoint p)
        {
            var point = new PointCm(p.X, p.Z);
            if (list.Count == 0 || list[^1] != point) list.Add(point);
        }
    }

    /// <summary>Drops every second inner point until the list fits (ends kept).</summary>
    private static List<PointCm> Thin(List<PointCm> points, int max)
    {
        while (points.Count > Math.Max(2, max))
            points = points.Where((p, i) => i == 0 || i == points.Count - 1 || i % 2 == 0).ToList();
        return points;
    }

    private static long Measure(SimContext ctx, WayKind kind, List<PointCm> points) =>
        WayGeometry.Build(ctx.Terrain, kind, points, ctx.State.TrailRules.SegmentLengthMeters * 100, ctx.State.TrailRules.PathGradingMeters).LengthCm;

    /// <summary>"Old Piste" → "Old Piste 2" (or the next free number).</summary>
    private static string NextName(WorldState state, string name)
    {
        string stem = name;
        int space = name.LastIndexOf(' ');
        if (space > 0 && int.TryParse(name[(space + 1)..], out _)) stem = name[..space];
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} {n}";
            if (state.Ways.All(w => w.Name != candidate)) return candidate;
        }
    }
}
