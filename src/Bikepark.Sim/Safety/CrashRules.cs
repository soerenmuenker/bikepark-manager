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

    /// <summary>
    /// Share of the would-be minor crashes that only break the bike (permille; 0 = never): no injury, a little mood, then
    /// the service shop (or a new rental bike) or home.
    /// </summary>
    public int BikeOnlyPermille { get; set; }

    /// <summary>Protectors (pro shop) cut the share of serious crashes to this (permille of it).</summary>
    public int ProtectedSeriousPermille { get; set; } = 500;

    /// <summary>Alcohol makes riders sloppy: the chance rises by up to this at full alcohol (1000), linearly.</summary>
    public int AlcoholFactorPermille { get; set; } = 2000;

    /// <summary>
    /// Two riders passing a crossing of their ways (or of a way and a T-bar track) who are both within this distance of the
    /// crossing point at the same moment collide, when at least one of them is as fast as <see cref="CollisionMinSpeedCmPerS"/>.
    /// No dice: close contact is a crash. 0 turns collisions off.
    /// </summary>
    public int CollisionDistanceCm { get; set; } = 100;

    /// <summary>Riders slower than this (climbing, towed, hurt) only collide with someone at least this fast.</summary>
    public int CollisionMinSpeedCmPerS { get; set; } = 350;

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

    /// <summary>
    /// A crash closes its trail to new riders: a minor one for this long, a serious one until the helicopter has flown
    /// the rider out. Riders already on the trail finish their run.
    /// </summary>
    public int MinorCloseMinutes { get; set; } = 5;

    // ---- Crash rate (reviews) ----

    /// <summary>Closed days kept in the accident book and in each trail's crash rate.</summary>
    public int RateDays { get; set; } = 7;

    /// <summary>
    /// Crashes per 10 000 runs a trail of each rating may have before guests feel it is unsafe. Crossings, a wrong rating
    /// or worn features push a trail above it.
    /// </summary>
    public Reputation.PerRating ExpectedCrashesPer10kRunsByRating { get; set; } = new() { Green = 10, Blue = 20, Red = 40, Black = 60 };

    /// <summary>
    /// The rate is never taken over fewer runs than this, so a single crash on a quiet trail is no pattern (1000: one crash
    /// is at most 10 per 10 000 runs, which every rating allows).
    /// </summary>
    public int RateMinRuns { get; set; } = 1000;

    /// <summary>
    /// Safety (permille) a guest takes off their review per crash per 10 000 runs above the expected rate, for the worst
    /// trail they rode, up to <see cref="MaxRateSafetyLoss"/>.
    /// </summary>
    public int SafetyLossPerExtraCrashPer10k { get; set; } = 4;
    public int MaxRateSafetyLoss { get; set; } = 500;

    public List<string> Validate()
    {
        var errors = new List<string>();
        const string p = "crashRules";
        if (FeatureBasePpm is < 0 or > 1_000_000 || TerrainBasePpm is < 0 or > 1_000_000)
            errors.Add($"{p}: chances (ppm) must be within 0..1000000");
        if (new[] { JumpFactorPermille, OverSkillPer100Permille, WornFactorPermille, WetFactorPermille, FatigueFactorPermille, AlcoholFactorPermille }.Any(f => f is < 0 or > 100_000))
            errors.Add($"{p}: factors must be within 0..100000");
        if (MinSkillFactorPermille is < 0 or > 1000) errors.Add($"{p}.minSkillFactorPermille must be within 0..1000");
        if (FatigueBelowEnergy is < 1 or > 1000) errors.Add($"{p}.fatigueBelowEnergy must be within 1..1000");
        if (BikeOnlyPermille is < 0 or > 1000 || ProtectedSeriousPermille is < 0 or > 1000)
            errors.Add($"{p}: bikeOnlyPermille and protectedSeriousPermille must be within 0..1000");
        if (CollisionDistanceCm is < 0 or > 10_000) errors.Add($"{p}.collisionDistanceCm must be within 0..10000");
        if (CollisionMinSpeedCmPerS is < 0 or > 10_000) errors.Add($"{p}.collisionMinSpeedCmPerS must be within 0..10000");
        if (new[] { SeriousBasePermille, SeriousJumpPermille, SeriousCollisionPermille, SeriousPer100OverSkillPermille }.Any(s => s is < 0 or > 1000))
            errors.Add($"{p}: serious shares must be within 0..1000");
        if (MinorMoodLoss is < 0 or > 1000 || SeriousMoodLoss is < 0 or > 1000) errors.Add($"{p}: mood losses must be within 0..1000");
        if (MinorSpeedCmPerS is < 10 or > 2000) errors.Add($"{p}.minorSpeedCmPerS must be within 10..2000");
        if (TowStopMinutes is < 0 or > 600) errors.Add($"{p}.towStopMinutes must be within 0..600");
        if (HelicopterMinMinutes < 1 || HelicopterMaxMinutes < HelicopterMinMinutes || HelicopterMaxMinutes > 600)
            errors.Add($"{p}: 1 <= helicopterMinMinutes <= helicopterMaxMinutes <= 600");
        if (InjuredReviewChancePermille is < 0 or > 1000) errors.Add($"{p}.injuredReviewChancePermille must be within 0..1000");
        if (MinorCloseMinutes is < 0 or > 600) errors.Add($"{p}.minorCloseMinutes must be within 0..600");
        if (RateDays is < 1 or > 365) errors.Add($"{p}.rateDays must be within 1..365");
        if (ExpectedCrashesPer10kRunsByRating.Any(v => v is < 0 or > 10_000)) errors.Add($"{p}.expectedCrashesPer10kRunsByRating must be within 0..10000");
        if (RateMinRuns is < 1 or > 1_000_000) errors.Add($"{p}.rateMinRuns must be within 1..1000000");
        if (SafetyLossPerExtraCrashPer10k is < 0 or > 1000) errors.Add($"{p}.safetyLossPerExtraCrashPer10k must be within 0..1000");
        if (MaxRateSafetyLoss is < 0 or > 1000) errors.Add($"{p}.maxRateSafetyLoss must be within 0..1000");
        return errors;
    }
}
