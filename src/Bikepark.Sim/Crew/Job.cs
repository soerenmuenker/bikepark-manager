using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Crew;

/// <summary>A worker. All workers are alike (same wage and speed); tools make the whole crew faster.</summary>
public sealed class CrewMember
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>The job the worker is on (0 = none, e.g. outside work hours or nothing workable).</summary>
    public int JobId { get; set; }
}

public enum JobKind : byte
{
    /// <summary>Build a planned path or trail (<see cref="Job.WayId"/>).</summary>
    BuildWay = 0,

    /// <summary>Build a planned trail feature (<see cref="Job.WayId"/>, <see cref="Job.FeatureId"/>).</summary>
    BuildFeature = 1,

    /// <summary>Fell the trees in a circle (<see cref="Job.Center"/>, <see cref="Job.RadiusCm"/>).</summary>
    FellTrees = 2,

    /// <summary>Repair a worn feature (<see cref="Job.WayId"/>, <see cref="Job.FeatureId"/>): when done it is in perfect condition. The trail is closed meanwhile.</summary>
    RepairFeature = 3,
}

/// <summary>
/// A piece of crew work, in the queue in priority order. A job first fells its <see cref="Trees"/> (one at a time, each
/// adds wood), then does its <see cref="WorkMinutes"/> of <see cref="MainWorkType"/> work. Progress is counted in
/// permille crew-minutes (a worker at base speed adds 1000 per minute) and restarts at 0 for every tree and for the main work.
/// </summary>
public sealed class Job
{
    public int Id { get; set; }
    public JobKind Kind { get; set; }

    public int WayId { get; set; }
    public int FeatureId { get; set; }

    /// <summary>Workers the player assigned (repair jobs); 0 = as many as the rules allow.</summary>
    public int Workers { get; set; }

    /// <summary>Felling area (FellTrees jobs only).</summary>
    public PointCm Center { get; set; }
    public int RadiusCm { get; set; }

    /// <summary>Trees to cut before the main work, in felling order (positions identify scatter trees).</summary>
    public List<PointCm> Trees { get; set; } = [];
    public int TreesFelled { get; set; }

    /// <summary>Crew-minutes of main work at base speed (0 for pure felling jobs).</summary>
    public long WorkMinutes { get; set; }
    public WorkType MainWorkType { get; set; }

    /// <summary>Progress on the current tree or on the main work, in permille crew-minutes.</summary>
    public long Progress { get; set; }

    /// <summary>Wood the job needs; taken from the stock when the first worker starts.</summary>
    public int Wood { get; set; }
    public bool WoodTaken { get; set; }

    public bool IsFelling => TreesFelled < Trees.Count;

    /// <summary>The kind of work needed now.</summary>
    public WorkType CurrentWorkType => IsFelling ? WorkType.Felling : MainWorkType;
}

/// <summary>Wood and work counters (for reports).</summary>
public sealed class CrewStats
{
    public long TreesFelled { get; set; }
    public long WoodFelled { get; set; }
    public long WoodBought { get; set; }
    public long WoodUsed { get; set; }
    public long JobsCompleted { get; set; }
    public long CrewMinutesWorked { get; set; }
}
