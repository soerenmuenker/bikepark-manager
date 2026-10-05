namespace Bikepark.Sim.Trails;

/// <summary>What a way is for. Access paths are gentle two-way gravel paths riders climb on; trails are one-way
/// downhill lines.</summary>
public enum WayKind : byte
{
    AccessPath = 0,
    Trail = 1,
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

    public WayStats Stats { get; set; } = new();
}

/// <summary>Usage counters of a trail.</summary>
public sealed class WayStats
{
    public long Runs { get; set; }
    public int RunsToday { get; set; }
    public long SumRunMinutes { get; set; }

    /// <summary>Sum of each run's average fun (0..1000).</summary>
    public long SumFun { get; set; }
}

/// <summary>Building rules and rider tuning, loaded from the scenario and saved with the game.</summary>
public sealed class TrailRules
{
    public int SegmentLengthMeters { get; set; } = 10;
    public int SnapRadiusMeters { get; set; } = 12;
    public int MinLengthMeters { get; set; } = 30;
    public int MaxLengthMeters { get; set; } = 4_000;
    public int MaxControlPoints { get; set; } = 200;

    /// <summary>Steepest allowed 10 m segment of an access path, either direction (150 = 15 %).</summary>
    public int PathMaxGradePermille { get; set; } = 150;

    public int TrailMaxDownGradePermille { get; set; } = 700;
    public int TrailMaxUpGradePermille { get; set; } = 80;

    /// <summary>Full corridor width cleared of trees and rocks.</summary>
    public int PathCorridorCm { get; set; } = 400;
    public int TrailCorridorCm { get; set; } = 200;

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
        if (PathMaxGradePermille <= 0 || TrailMaxDownGradePermille <= 0 || TrailMaxUpGradePermille < 0)
            errors.Add("trailRules grade limits must be positive");
        if (PathCorridorCm <= 0 || TrailCorridorCm <= 0) errors.Add("trailRules corridor widths must be positive");
        if (ClimbSpeedMinCmPerS <= 0 || ClimbSpeedMaxCmPerS < ClimbSpeedMinCmPerS) errors.Add("trailRules climb speeds are inconsistent");
        if (DescentSpeedMinCmPerS <= 0 || DescentSpeedMaxCmPerS < DescentSpeedMinCmPerS) errors.Add("trailRules descent speeds are inconsistent");
        if (ClimbEnergyPerMeterGain < 0 || DescentEnergyPer100Meters < 0 || TiredEnergy is < 0 or > 1000)
            errors.Add("trailRules energy values are out of range");
        return errors;
    }
}
