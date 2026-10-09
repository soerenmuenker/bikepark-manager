namespace Bikepark.Sim.Safety;

/// <summary>Accidents of one closed day.</summary>
public sealed record AccidentDay(long Day, int Minor, int Serious);

/// <summary>Crash counts and the accident history the insurance premium is based on. Saved with the game.</summary>
public sealed class SafetyState
{
    public long TotalCrashes { get; set; }
    public long TotalSerious { get; set; }
    public long TotalCollisions { get; set; }

    public int MinorToday { get; set; }
    public int SeriousToday { get; set; }

    /// <summary>Closed days, oldest first, at most <see cref="CrashRules.InsuranceDays"/>.</summary>
    public List<AccidentDay> History { get; set; } = [];

    /// <summary>Sum of minutes seriously injured riders waited for the helicopter (divide by <see cref="Evacuations"/>).</summary>
    public long SumRescueMinutes { get; set; }
    public long Evacuations { get; set; }

    public long TotalInsuranceCents { get; set; }
    public long InsuranceTodayCents { get; set; }
}
