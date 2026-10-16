namespace Bikepark.Sim.Safety;

/// <summary>Accidents of one closed day.</summary>
public sealed record AccidentDay(long Day, int Minor, int Serious);

/// <summary>
/// A rider passed a crossing point: on way (or towed lift) <paramref name="WayId"/> at <paramref name="Cm"/> along it,
/// at <paramref name="TimeMs"/> (game time: tick × 60 000 + ms into the minute) and <paramref name="SpeedCmPerS"/>.
/// </summary>
public sealed record CrossingPassage(long TimeMs, int GuestId, int WayId, long Cm, int SpeedCmPerS);

/// <summary>Crash counts and the accident history. Saved with the game.</summary>
public sealed class SafetyState
{
    /// <summary>
    /// Crossing passages of this minute and the one before, in the order they were recorded: a rider passing a crossing
    /// checks them for the riders on the other way (<see cref="CrashMath.Collide"/>). Pruned every minute.
    /// </summary>
    public List<CrossingPassage> Passages { get; set; } = [];

    public long TotalCrashes { get; set; }
    public long TotalSerious { get; set; }
    public long TotalCollisions { get; set; }

    /// <summary>Crashes of riders who had been drinking (at least <see cref="Facilities.FacilityRules.TipsyAlcohol"/>).</summary>
    public long CrashesUnderInfluence { get; set; }

    public int MinorToday { get; set; }
    public int SeriousToday { get; set; }

    /// <summary>Closed days, oldest first, at most <see cref="CrashRules.RateDays"/>.</summary>
    public List<AccidentDay> History { get; set; } = [];

    /// <summary>Sum of minutes seriously injured riders waited for the helicopter (divide by <see cref="Evacuations"/>).</summary>
    public long SumRescueMinutes { get; set; }
    public long Evacuations { get; set; }
}
