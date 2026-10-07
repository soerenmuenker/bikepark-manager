namespace Bikepark.Sim.Trails;

/// <summary>What a way is for. Access paths are gentle two-way gravel paths riders climb on; trails are one-way
/// downhill lines.</summary>
public enum WayKind : byte
{
    AccessPath = 0,
    Trail = 1,
}

/// <summary>Who made a way. Scenario ways (e.g. the existing hiking route) behave like player ways for now.</summary>
public enum WayOrigin : byte
{
    Player = 0,
    Scenario = 1,
}

/// <summary>A horizontal position in centimeters (X east, Z south).</summary>
public readonly record struct PointCm(int X, int Z);

/// <summary>Where a way's endpoint attaches to another way: that way's id and the distance along it.</summary>
public sealed record WayJoin(int WayId, long DistanceCm);

/// <summary>
/// A path or trail the player built. Only the player's input is stored (oriented, snapped control points and
/// joins); geometry, segments and the routing graph are derived (<see cref="WayNetwork"/>).
/// </summary>
public sealed class Way
{
    public int Id { get; set; }
    public WayKind Kind { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Control points from start to end. Paths start at their low end, trails at their high end.</summary>
    public List<PointCm> Points { get; set; } = [];

    public WayJoin? StartJoin { get; set; }
    public WayJoin? EndJoin { get; set; }

    /// <summary>Hub (station plateau, parking lot) the start / end attaches to instead of a way; 0 = none.</summary>
    public int StartHubId { get; set; }
    public int EndHubId { get; set; }

    public WayOrigin Origin { get; set; }

    /// <summary>Features placed on a trail (berms, jumps, wood features), sorted by distance. Empty on access paths.</summary>
    public List<TrailFeature> Features { get; set; } = [];

    /// <summary>False while the way is only planned (a crew job builds it); riders only use built ways.</summary>
    public bool Built { get; set; } = true;

    public WayStats Stats { get; set; } = new();

    // ---- Closures (trails only; the wear itself is on the features, see TrailCondition) ----

    /// <summary>Closed by the player: riders don't start runs on it.</summary>
    public bool Closed { get; set; }

    /// <summary>Closed because a feature wore out (condition 0); reopens when a repair job finishes that leaves none at 0.</summary>
    public bool WornOut { get; set; }

    /// <summary>The crew is repairing a feature: the trail is closed until the repair is done.</summary>
    public bool Repairing { get; set; }

    /// <summary>A built trail riders may start runs on.</summary>
    public bool IsRideable => Built && !Closed && !WornOut && !Repairing;
}

/// <summary>Usage counters of a trail.</summary>
public sealed class WayStats
{
    public long Runs { get; set; }
    public int RunsToday { get; set; }
    public long SumRunMinutes { get; set; }

    /// <summary>Sum of each run's average fun (0..1000).</summary>
    public long SumFun { get; set; }

    /// <summary>Minutes the trail was closed (by the player, worn out or under repair) while the park was open.</summary>
    public long ClosedMinutes { get; set; }

    public int Repairs { get; set; }
}

/// <summary>Building rules and rider tuning, loaded from the scenario and saved with the game.</summary>
public sealed class TrailRules
{
    public int SegmentLengthMeters { get; set; } = 10;
    public int SnapRadiusMeters { get; set; } = 12;
    public int MinLengthMeters { get; set; } = 30;
    public int MaxLengthMeters { get; set; } = 4_000;
    public int MaxControlPoints { get; set; } = 200;

    /// <summary>
    /// Gravel paths are graded (cut and fill): their surface is the ground smoothed over ± this many meters, so
    /// local bumps don't count against the gradient limits. Trails follow the ground.
    /// </summary>
    public int PathGradingMeters { get; set; } = WayGeometry.DefaultGradingMeters;

    // Gradient limits per 10 m segment, in tenths of the -10..+10 gradient score (see Gradient; 30 = 3.0 = 27°).
    // Beyond "Max" a way cannot be built; beyond "Steep" the planner only warns (riders feel it, it still builds).

    /// <summary>Steepest gravel path segment, either direction.</summary>
    public int PathMaxGradient { get; set; } = 40;
    public int PathSteepGradient { get; set; } = 20;

    /// <summary>Steepest drop on a trail (as a positive number).</summary>
    public int TrailMaxDropGradient { get; set; } = 80;
    public int TrailSteepDropGradient { get; set; } = 50;

    /// <summary>Steepest climb on a trail.</summary>
    public int TrailMaxClimbGradient { get; set; } = 30;
    public int TrailSteepClimbGradient { get; set; } = 15;

    /// <summary>Full corridor width cleared of trees and rocks.</summary>
    public int PathCorridorCm { get; set; } = 400;
    public int TrailCorridorCm { get; set; } = 200;

    // Trail features.
    public int FeatureStartMarginMeters { get; set; } = 10;
    public int FeatureEndMarginMeters { get; set; } = 15;

    /// <summary>Minimum free distance between two features on a trail.</summary>
    public int FeatureGapMeters { get; set; } = 5;

    public int MaxFeaturesPerTrail { get; set; } = 40;

    // Riders. Speeds in cm per second, energy on a 0..1000 scale.
    public int ClimbSpeedMinCmPerS { get; set; } = 110;
    public int ClimbSpeedMaxCmPerS { get; set; } = 300;
    public int DescentSpeedMinCmPerS { get; set; } = 300;
    public int DescentSpeedMaxCmPerS { get; set; } = 900;
    public int ClimbEnergyPerMeterGain { get; set; } = 2;
    public int DescentEnergyPer100Meters { get; set; } = 3;
    public int TiredEnergy { get; set; } = 150;

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (SegmentLengthMeters is < 2 or > 100) errors.Add("trailRules.segmentLengthMeters must be within 2..100");
        if (SnapRadiusMeters is < 1 or > 100) errors.Add("trailRules.snapRadiusMeters must be within 1..100");
        if (MinLengthMeters < 1 || MaxLengthMeters <= MinLengthMeters) errors.Add("trailRules min/max length are inconsistent");
        if (MaxControlPoints < 2) errors.Add("trailRules.maxControlPoints must be >= 2");
        if (PathGradingMeters is < 0 or > 200) errors.Add("trailRules.pathGradingMeters must be within 0..200");
        foreach (var (name, steep, max) in new[]
                 {
                     ("path", PathSteepGradient, PathMaxGradient),
                     ("trail drop", TrailSteepDropGradient, TrailMaxDropGradient),
                     ("trail climb", TrailSteepClimbGradient, TrailMaxClimbGradient),
                 })
        {
            if (steep <= 0 || steep > max || max > Gradient.MaxTenths)
                errors.Add($"trailRules {name} gradients must satisfy 0 < steep <= max <= {Gradient.MaxTenths}");
        }
        if (PathCorridorCm <= 0 || TrailCorridorCm <= 0) errors.Add("trailRules corridor widths must be positive");
        if (FeatureStartMarginMeters < 0 || FeatureEndMarginMeters < 0 || FeatureGapMeters < 0 || MaxFeaturesPerTrail < 0)
            errors.Add("trailRules feature margins, gap and count must be >= 0");
        if (ClimbSpeedMinCmPerS <= 0 || ClimbSpeedMaxCmPerS < ClimbSpeedMinCmPerS) errors.Add("trailRules climb speeds are inconsistent");
        if (DescentSpeedMinCmPerS <= 0 || DescentSpeedMaxCmPerS < DescentSpeedMinCmPerS) errors.Add("trailRules descent speeds are inconsistent");
        if (ClimbEnergyPerMeterGain < 0 || DescentEnergyPer100Meters < 0 || TiredEnergy is < 0 or > 1000)
            errors.Add("trailRules energy values are out of range");
        return errors;
    }
}
