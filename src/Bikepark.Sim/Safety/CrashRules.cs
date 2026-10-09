namespace Bikepark.Sim.Safety;

public enum CrashCause : byte
{
    /// <summary>On a trail feature (jump, drop, berm, ...).</summary>
    Feature = 0,

    /// <summary>On rough or steep trail (rocks, roots, gradient).</summary>
    Terrain = 1,

    /// <summary>Hit another rider where two ways cross.</summary>
    Collision = 2,
}

public enum InjurySeverity : byte
{
    None = 0,

    /// <summary>The rider gets up and rides down slowly, then goes home.</summary>
    Minor = 1,

    /// <summary>The rider stays where they fell until the rescue helicopter flies them out.</summary>
    Serious = 2,
}

/// <summary>
/// Crash tuning, loaded from the scenario and saved with the game. Off by default: nobody crashes and no randomness is
/// used, so older saves and scenarios behave as before. Chances are per pass in parts per million and scaled by factors
/// in permille (1000 = unchanged); see <see cref="CrashMath"/>.
/// </summary>
public sealed class CrashRules
{
    public bool Enabled { get; set; }

    // ---- Chances ----

    /// <summary>Chance per feature ridden, at a difficulty equal to the rider's skill.</summary>
    public int FeatureBasePpm { get; set; } = 400;

    /// <summary>Extra factor for features riders take off on (tables, doubles, kickers, drops).</summary>
    public int JumpFactorPermille { get; set; } = 2000;

    /// <summary>Chance per 10 m trail segment, at a terrain difficulty equal to the rider's skill.</summary>
    public int TerrainBasePpm { get; set; } = 4;

    /// <summary>
    /// How much each 100 points of difficulty above (below) the rider's skill raise (lower) the chance, in permille of
    /// the base; never below <see cref="MinSkillFactorPermille"/>.
    /// </summary>
    public int OverSkillPer100Permille { get; set; } = 1500;
    public int MinSkillFactorPermille { get; set; } = 150;

    /// <summary>Below the feature's rough level (<see cref="Trails.WearRules.RoughBelowPermille"/>) the chance rises, up to 1 + this at 0 %.</summary>
    public int WornFactorPermille { get; set; } = 2000;

    /// <summary>Extra chance on fully wet ground (scaled by wetness).</summary>
    public int WetFactorPermille { get; set; } = 1000;

    /// <summary>Below this energy riders get sloppy, up to 1 + <see cref="FatigueFactorPermille"/> at 0.</summary>
    public int FatigueBelowEnergy { get; set; } = 350;
    public int FatigueFactorPermille { get; set; } = 1000;

    /// <summary>Chance of a collision when passing a crossing while a rider on the other way is within <see cref="CrossingWindowCm"/> of it.</summary>
    public int CollisionPpm { get; set; } = 60_000;
    public int CrossingWindowCm { get; set; } = 1_500;

    // ---- Severity ----

    /// <summary>Share of crashes that are serious, plus extras for jumps, collisions and per 100 points over skill.</summary>
    public int SeriousBasePermille { get; set; } = 80;
    public int SeriousJumpPermille { get; set; } = 80;
    public int SeriousCollisionPermille { get; set; } = 150;
    public int SeriousPer100OverSkillPermille { get; set; } = 60;

    /// <summary>Mood lost on crashing.</summary>
    public int MinorMoodLoss { get; set; } = 250;
    public int SeriousMoodLoss { get; set; } = 600;

    /// <summary>
    /// A rider on a T-bar hurt in a collision stops the lift: a minor crash for this long (they get off the track and go
    /// home), a serious one until the helicopter has flown them out.
    /// </summary>
    public int TowStopMinutes { get; set; } = 5;

    /// <summary>Riding speed of a rider with a minor injury on the way down.</summary>
    public int MinorSpeedCmPerS { get; set; } = 150;

    /// <summary>Minutes until the rescue helicopter has flown a seriously injured rider out (rolled per call).</summary>
    public int HelicopterMinMinutes { get; set; } = 25;
    public int HelicopterMaxMinutes { get; set; } = 50;

    /// <summary>Chance that a guest who crashed writes a review (others: <see cref="Reputation.ReputationRules.ReviewChancePermille"/>).</summary>
    public int InjuredReviewChancePermille { get; set; } = 900;

    // ---- Insurance ----

    /// <summary>Daily premium: a base plus a charge per accident in the last <see cref="InsuranceDays"/> days.</summary>
    public long InsuranceBaseCents { get; set; } = 5_000;
    public long InsurancePerMinorCents { get; set; } = 1_500;
    public long InsurancePerSeriousCents { get; set; } = 15_000;
    public int InsuranceDays { get; set; } = 7;

    public List<string> Validate()
    {
        var errors = new List<string>();
        const string p = "crashRules";
        if (FeatureBasePpm is < 0 or > 1_000_000 || TerrainBasePpm is < 0 or > 1_000_000 || CollisionPpm is < 0 or > 1_000_000)
            errors.Add($"{p}: chances (ppm) must be within 0..1000000");
        if (new[] { JumpFactorPermille, OverSkillPer100Permille, WornFactorPermille, WetFactorPermille, FatigueFactorPermille }.Any(f => f is < 0 or > 100_000))
            errors.Add($"{p}: factors must be within 0..100000");
        if (MinSkillFactorPermille is < 0 or > 1000) errors.Add($"{p}.minSkillFactorPermille must be within 0..1000");
        if (FatigueBelowEnergy is < 1 or > 1000) errors.Add($"{p}.fatigueBelowEnergy must be within 1..1000");
        if (CrossingWindowCm is < 0 or > 100_000) errors.Add($"{p}.crossingWindowCm must be within 0..100000");
        if (new[] { SeriousBasePermille, SeriousJumpPermille, SeriousCollisionPermille, SeriousPer100OverSkillPermille }.Any(s => s is < 0 or > 1000))
            errors.Add($"{p}: serious shares must be within 0..1000");
        if (MinorMoodLoss is < 0 or > 1000 || SeriousMoodLoss is < 0 or > 1000) errors.Add($"{p}: mood losses must be within 0..1000");
        if (MinorSpeedCmPerS is < 10 or > 2000) errors.Add($"{p}.minorSpeedCmPerS must be within 10..2000");
        if (TowStopMinutes is < 0 or > 600) errors.Add($"{p}.towStopMinutes must be within 0..600");
        if (HelicopterMinMinutes < 1 || HelicopterMaxMinutes < HelicopterMinMinutes || HelicopterMaxMinutes > 600)
            errors.Add($"{p}: 1 <= helicopterMinMinutes <= helicopterMaxMinutes <= 600");
        if (InjuredReviewChancePermille is < 0 or > 1000) errors.Add($"{p}.injuredReviewChancePermille must be within 0..1000");
        if (InsuranceBaseCents < 0 || InsurancePerMinorCents < 0 || InsurancePerSeriousCents < 0) errors.Add($"{p}: insurance must be >= 0");
        if (InsuranceDays is < 1 or > 365) errors.Add($"{p}.insuranceDays must be within 1..365");
        return errors;
    }
}
