using Bikepark.Sim.Events;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Trails;

/// <summary>
/// Editing built ways: renaturalizing a section of a trail or gravel path (the way is gone there, trees grow back),
/// joining a newly built trail onto loose trail ends (it becomes part of them), and splitting a trail where a new gravel
/// path attaches (gravel routes divide trails). A trail cut in two becomes "&lt;name&gt; Part 1" and "&lt;name&gt; Part 2".
/// Only stored input changes: control points (re-sampled from the derived geometry), joins, features and closures;
/// everything else is derived again.
/// </summary>
public static class WayEditing
{
    /// <summary>How close to a way's end a join counts as "at the end".</summary>
    public const long EndToleranceCm = 150;

    /// <summary>Spacing of the control points re-sampled from a trail's geometry.</summary>
    private const long SampleCm = 1_000;

    /// <summary>Leftovers shorter than this go with the removed section.</summary>
    private const long MinPieceCm = 100;

    private const string PartWord = " Part ";

    /// <summary>Why a way can't be renaturalized now, or null.</summary>
    public static string? CannotEdit(WorldState state, Way? way)
    {
        if (way is null) return "No such trail or path.";
        if (!way.Built) return $"{way.Label} is only planned: cancel its job in the Crew menu instead.";
        if (state.Jobs.Any(j => j.WayId == way.Id)) return $"The crew is working on {way.Label}: wait or cancel the job first.";
        return null;
    }

    /// <summary>Why the section can't be renaturalized, or null.</summary>
    public static string? CannotRenaturalize(WorldState state, WayNetwork network, Way? way, long fromCm, long toCm)
    {
        if (CannotEdit(state, way) is { } reason) return reason;
        if (!network.TryGetGeometry(way!.Id, out var geometry)) return "No such trail or path.";
        if (fromCm < 0 || toCm > geometry.LengthCm || toCm - fromCm < 100) return "Mark a section along the way.";
        if (Land.LandMath.OwnedPredicate(state) is { } owned)
            for (int i = 0; i < geometry.SampleCount; i++)
                if (geometry.Distances[i] >= fromCm && geometry.Distances[i] <= toCm && !owned(geometry.Xs[i], geometry.Zs[i]))
                    return "Not your land: only sections on the park's land can be renaturalized.";
        return null;
    }

    // ---------------------------------------------------------------- split (where a gravel path attaches)

    /// <summary>A gravel path attaching here may split the trail (built, nobody working on it, not on a feature or an end).</summary>
    private static bool CanSplit(WorldState state, WayNetwork network, Way trail, long atCm)
    {
        if (!trail.Built || state.Jobs.Any(j => j.WayId == trail.Id) || !network.TryGetGeometry(trail.Id, out var geometry)) return false;
        if (atCm <= EndToleranceCm || atCm >= geometry.LengthCm - EndToleranceCm) return false;
        return !network.FeaturesOn(trail.Id).Any(f => f.StartCm < atCm && atCm < f.EndCm);
    }

    /// <summary>Splits a trail at a distance: the upper part keeps the trail (stats), the lower one is a new trail.</summary>
    private static Way Split(SimContext ctx, Way way, long atCm)
    {
        var state = ctx.State;
        var geometry = ctx.Network.Geometry(way.Id);
        long length = geometry.LengthCm;
        var upper = Resample(state, geometry, 0, atCm);
        var lower = Resample(state, geometry, atCm, length);
        long upperLength = Measure(ctx, way.Kind, upper);

        var (upperName, lowerName) = PartNames(state, way);
        var second = NewPiece(state, way, lower, lowerName);
        way.Name = upperName;
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
    /// Removes a section of a trail or gravel path. What is left above keeps the way; what is left below becomes a new
    /// one (trails: "Part 1" / "Part 2"); both have a loose end there (a trail stays closed until reconnected). Ways that
    /// joined the removed section get a loose end.
    /// </summary>
    public static void Renaturalize(SimContext ctx, Way way, long fromCm, long toCm)
    {
        var state = ctx.State;
        var geometry = ctx.Network.Geometry(way.Id);
        long length = geometry.LengthCm;
        bool keepUpper = fromCm >= MinPieceCm, keepLower = toCm <= length - MinPieceCm;
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
            var (upperName, lowerName) = way.Kind == WayKind.Trail ? PartNames(state, way) : (way.Name, "");
            lower = NewPiece(state, way, Resample(state, geometry, toCm, length), lowerName);
            way.Name = upperName;
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
            // Only the lower part is left: it keeps the way, starting loose.
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

    /// <summary>
    /// Names for a trail cut in two: "Red Rocket" → "Red Rocket Part 1" + "Red Rocket Part 2"; a part keeps its name and
    /// the new one gets the next free number ("Red Rocket Part 2" → itself + "Red Rocket Part 3").
    /// </summary>
    public static (string Upper, string Lower) PartNames(WorldState state, Way way)
    {
        var (stem, number) = PartOf(way.Name.Length > 0 ? way.Name : way.Label);
        string upper = number > 0 ? way.Name : FreePart(state, stem, 1, way, null);
        string lower = FreePart(state, stem, Math.Max(number, 1) + 1, way, upper);
        return (upper, lower);
    }

    /// <summary>"Red Rocket Part 2" → ("Red Rocket", 2); any other name → (name, 0).</summary>
    public static (string Stem, int Number) PartOf(string name)
    {
        int at = name.LastIndexOf(PartWord, StringComparison.Ordinal);
        return at > 0 && int.TryParse(name[(at + PartWord.Length)..], out int n) && n > 0 ? (name[..at], n) : (name, 0);
    }

    private static string FreePart(WorldState state, string stem, int from, Way except, string? taken)
    {
        for (int n = from; ; n++)
        {
            string candidate = $"{stem}{PartWord}{n}";
            if (candidate != taken && state.Ways.All(w => w == except || w.Name != candidate)) return candidate;
        }
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
                if (CanSplit(state, ctx.Network, trail, join.DistanceCm))
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

    /// <summary>
    /// The planned trail continues an existing trail's end or leads into a trail's start: once built it becomes part of
    /// that trail (if the end is loose), so it needs no name of its own.
    /// </summary>
    public static bool ContinuesTrail(WayNetwork network, WayPlan plan) =>
        plan.Kind == WayKind.Trail
        && (plan.StartJoin is { } s && network.FindWay(s.WayId) is { Kind: WayKind.Trail } && network.TryGetGeometry(s.WayId, out var a)
            && s.DistanceCm >= a.LengthCm - EndToleranceCm
            || plan.EndJoin is { } e && network.FindWay(e.WayId) is { Kind: WayKind.Trail } && e.DistanceCm <= EndToleranceCm);

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
        // The last two parts of a trail joined again: it gets its old name back.
        var (stem, number) = PartOf(trail.Name);
        if (number > 0 && PartOf(next.Name).Stem == stem && !state.Ways.Any(w => w != trail && (PartOf(w.Name).Stem == stem && PartOf(w.Name).Number > 0 || w.Name == stem)))
            trail.Name = stem;
        state.WaysRevision++;
        Systems.RiderSystem.ResetRidersUsing(ctx, trail.Id, next.Id);
        ctx.Publish(new TrailsJoined(ctx.Tick, trail.Id, next.Id));
        ctx.Publish(new WayDeleted(ctx.Tick, next.Id));
    }

    // ---------------------------------------------------------------- helpers

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
}
