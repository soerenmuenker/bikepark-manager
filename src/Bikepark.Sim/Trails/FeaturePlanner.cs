namespace Bikepark.Sim.Trails;

/// <summary>The result of planning a trail feature: where it would go and what is wrong with it.</summary>
public sealed class FeaturePlan
{
    public required int WayId { get; init; }
    public TrailFeatureType? Type { get; init; }

    /// <summary>The stretch of trail it would cover (0..0 if the trail or type is unknown).</summary>
    public long StartCm { get; init; }
    public long EndCm { get; init; }

    /// <summary>Where its landing ends (= <see cref="EndCm"/> without a landing).</summary>
    public long LandingEndCm { get; init; }

    public required List<WayIssue> Issues { get; init; }

    public bool IsValid => Issues.All(i => i.Severity != IssueSeverity.Error);

    public string? FirstError => Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message;
}

/// <summary>
/// Validates placing a feature on a trail. Pure and read-only: the place command uses it, and the view calls it every
/// frame for the live preview, so the two can never disagree.
/// </summary>
public static class FeaturePlanner
{
    public static FeaturePlan Plan(
        WayNetwork network, TrailRules rules, IReadOnlyList<TrailFeatureType> types, int wayId, string typeId, long distanceCm)
    {
        var type = TrailFeatures.FindType(types, typeId);
        if (type is null)
            return Fail(wayId, null, "unknownType", $"Unknown feature '{typeId}'.");
        var way = network.FindWay(wayId);
        if (way is null || !network.TryGetGeometry(wayId, out var geometry))
            return Fail(wayId, type, "noSuchTrail", "No such trail.");
        if (way.Kind != WayKind.Trail)
            return Fail(wayId, type, "notATrail", "Features go on trails, not on gravel paths.");

        long start = distanceCm, end = distanceCm + type.LengthCm, zoneEnd = distanceCm + type.ZoneCm;
        var issues = new List<WayIssue>();
        if (start < rules.FeatureStartMarginMeters * 100L)
            issues.Add(Error("tooCloseToStart", $"Leave {rules.FeatureStartMarginMeters} m after the trail start.", start, end));
        if (zoneEnd > geometry.LengthCm - rules.FeatureEndMarginMeters * 100L)
            issues.Add(Error("tooCloseToEnd", type.LandingMeters > 0
                ? $"Leave {rules.FeatureEndMarginMeters} m before the trail end, after the {type.LandingMeters} m landing."
                : $"Leave {rules.FeatureEndMarginMeters} m before the trail end.", start, zoneEnd));
        if (way.Features.Count >= rules.MaxFeaturesPerTrail)
            issues.Add(Error("tooManyFeatures", $"At most {rules.MaxFeaturesPerTrail} features per trail."));

        long gap = rules.FeatureGapMeters * 100L;
        foreach (var other in TrailFeatures.Resolve(way, types))
        {
            if (other.StartCm - gap >= zoneEnd || other.ZoneEndCm + gap <= start) continue;
            string otherName = other.Type.Name.ToLowerInvariant();
            string message = other.Type.LandingMeters > 0 && start >= other.EndCm
                ? $"In the landing of the {otherName} at {other.StartCm / 100} m (keep its {other.Type.LandingMeters} m landing + {rules.FeatureGapMeters} m clear)."
                : type.LandingMeters > 0 && other.StartCm >= end
                    ? $"The {otherName} at {other.StartCm / 100} m is in the landing (keep {type.LandingMeters} m + {rules.FeatureGapMeters} m clear)."
                    : $"Too close to the {otherName} at {other.StartCm / 100} m (keep {rules.FeatureGapMeters} m apart).";
            issues.Add(Error("overlaps", message, start, zoneEnd));
            break;
        }

        if (issues.Count == 0)
            CheckTerrain(geometry, type, start, end, issues);

        return new FeaturePlan { WayId = wayId, Type = type, StartCm = start, EndCm = end, LandingEndCm = zoneEnd, Issues = issues };
    }

    /// <summary>
    /// Every covered segment within the gradient window; at least one turning enough if the type needs a bend; enough
    /// height lost over it if it needs steep ground; a downhill landing right after it if it needs one.
    /// </summary>
    private static void CheckTerrain(WayGeometry geometry, TrailFeatureType type, long start, long end, List<WayIssue> issues)
    {
        int steepest = int.MaxValue, flattest = int.MinValue, sharpest = 0;
        for (int i = geometry.SegmentIndexAt(start); i < geometry.Segments.Count; i++)
        {
            var segment = geometry.Segments[i];
            if (segment.StartCm >= end) break;
            steepest = Math.Min(steepest, segment.GradientTenths);
            flattest = Math.Max(flattest, segment.GradientTenths);
            sharpest = Math.Max(sharpest, segment.TurnPermille);
        }
        string name = type.Name.ToLowerInvariant();
        if (steepest < type.MinGradient)
            issues.Add(Error("tooSteep",
                $"Too steep for a {name}: {Gradient.Format(steepest)} (limit {Gradient.Format(type.MinGradient)}).", start, end));
        if (flattest > type.MaxGradient)
            issues.Add(Error("tooFlat", type.MaxGradient < 0
                ? $"A {name} needs a drop of at least {Gradient.Format(type.MaxGradient)} here ({Gradient.Format(flattest)})."
                : $"Climbs too much for a {name}: {Gradient.Format(flattest)} (limit {Gradient.Format(type.MaxGradient)}).", start, end));
        if (sharpest < type.MinTurn)
            issues.Add(Error("needsBend", $"A {name} needs a bend in the trail.", start, end));

        if (type.MinDropMeters > 0)
        {
            long dropCm = geometry.HeightAt(start) - geometry.HeightAt(end);
            if (dropCm < type.MinDropMeters * 100L)
                issues.Add(Error("tooShallow",
                    $"A {name} needs steep ground: at least {type.MinDropMeters} m down over its {type.LengthMeters} m (here {Math.Max(0, dropCm) / 100.0:0.0} m).", start, end));
        }

        if (type.LandingMeters > 0)
        {
            long landingEnd = end + type.LandingMeters * 100L;
            int landSteepest = int.MaxValue, landFlattest = int.MinValue;
            for (int i = geometry.SegmentIndexAt(end); i < geometry.Segments.Count; i++)
            {
                var segment = geometry.Segments[i];
                if (segment.StartCm >= landingEnd) break;
                if (segment.EndCm <= end) continue;
                landSteepest = Math.Min(landSteepest, segment.GradientTenths);
                landFlattest = Math.Max(landFlattest, segment.GradientTenths);
            }
            if (landSteepest < type.LandingMinGradient)
                issues.Add(Error("noLanding",
                    $"The landing is too steep: {Gradient.Format(landSteepest)} (limit {Gradient.Format(type.LandingMinGradient)}).", end, landingEnd));
            else if (landFlattest > type.LandingMaxGradient)
                issues.Add(Error("noLanding",
                    $"A {name} needs a downhill landing of {type.LandingMeters} m, at least {Gradient.Format(type.LandingMaxGradient)} " +
                    $"(here {Gradient.Format(landFlattest)}): a flat landing breaks wrists.", end, landingEnd));
        }
    }

    private static FeaturePlan Fail(int wayId, TrailFeatureType? type, string code, string message) =>
        new() { WayId = wayId, Type = type, Issues = [Error(code, message)] };

    private static WayIssue Error(string code, string message, long at = -1, long to = -1) =>
        new(IssueSeverity.Error, code, message, at, to);
}
