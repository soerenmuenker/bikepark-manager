using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Safety;

/// <summary>
/// Crash chances (pure, integer). Chances come back in parts per billion so small per-segment chances keep their
/// precision; roll them with <see cref="Roll"/>.
/// </summary>
public static class CrashMath
{
    public const int Billion = 1_000_000_000;

    /// <summary>Chance of crashing on a feature (condition in permille, 1000 = perfect).</summary>
    public static long FeatureChancePpb(CrashRules rules, WearRules wear, Guest guest, TrailFeatureType type, int conditionPermille, int wetnessPermille)
    {
        long ppb = rules.FeatureBasePpm * 1000L;
        if (Reputation.ReviewMath.IsJump(type.Kind)) ppb = ppb * rules.JumpFactorPermille / 1000;
        ppb = ppb * SkillFactor(rules, type.Difficulty, guest.Skill) / 1000;
        ppb = ppb * ConditionFactor(rules, wear, conditionPermille) / 1000;
        return Clamp(ppb * WetFactor(rules, wetnessPermille) / 1000 * FatigueFactor(rules, guest.Energy) / 1000 * AlcoholFactor(rules, guest.Alcohol) / 1000);
    }

    /// <summary>Chance of crashing on a 10 m segment of rough or steep trail.</summary>
    public static long TerrainChancePpb(CrashRules rules, Guest guest, WaySegment segment, int wetnessPermille)
    {
        long ppb = rules.TerrainBasePpm * 1000L * SkillFactor(rules, segment.TerrainDifficulty, guest.Skill) / 1000;
        return Clamp(ppb * WetFactor(rules, wetnessPermille) / 1000 * FatigueFactor(rules, guest.Energy) / 1000 * AlcoholFactor(rules, guest.Alcohol) / 1000);
    }

    /// <summary>
    /// Two passages of a crossing collide: each rider is within <see cref="CrashRules.CollisionDistanceCm"/> of the point
    /// for a moment around their passage (shorter the faster they are), the two moments overlap, and at least one of them
    /// rides at <see cref="CrashRules.CollisionMinSpeedCmPerS"/> or faster.
    /// </summary>
    public static bool Collide(CrashRules rules, CrossingPassage a, CrossingPassage b)
    {
        if (rules.CollisionDistanceCm <= 0 || Math.Max(a.SpeedCmPerS, b.SpeedCmPerS) < rules.CollisionMinSpeedCmPerS) return false;
        long apart = Math.Abs(a.TimeMs - b.TimeMs);
        long reach = rules.CollisionDistanceCm * 1000L / Math.Max(1, a.SpeedCmPerS) + rules.CollisionDistanceCm * 1000L / Math.Max(1, b.SpeedCmPerS);
        return apart <= reach;
    }

    /// <summary>
    /// Difficulty against skill: 1000 at a match, rising by <see cref="CrashRules.OverSkillPer100Permille"/> per 100
    /// points above the rider's skill, falling below it (never under <see cref="CrashRules.MinSkillFactorPermille"/>).
    /// </summary>
    public static int SkillFactor(CrashRules rules, int difficulty, int skill)
    {
        long over = difficulty - skill;
        long factor = over >= 0
            ? 1000 + over * rules.OverSkillPer100Permille / 100
            : 1000 * 1000 / (1000 - over * rules.OverSkillPer100Permille / 100);
        return (int)Math.Clamp(factor, rules.MinSkillFactorPermille, 1_000_000);
    }

    /// <summary>Worn features are riskier: 1000 down to the rough level, up to 1000 + <see cref="CrashRules.WornFactorPermille"/> at 0 %.</summary>
    public static int ConditionFactor(CrashRules rules, WearRules wear, int conditionPermille)
    {
        int rough = Math.Max(1, wear.RoughBelowPermille);
        if (conditionPermille >= rough) return 1000;
        return 1000 + (rough - Math.Max(0, conditionPermille)) * rules.WornFactorPermille / rough;
    }

    public static int WetFactor(CrashRules rules, int wetnessPermille) => 1000 + Math.Clamp(wetnessPermille, 0, 1000) * rules.WetFactorPermille / 1000;

    public static int FatigueFactor(CrashRules rules, int energy)
    {
        if (energy >= rules.FatigueBelowEnergy) return 1000;
        return 1000 + (rules.FatigueBelowEnergy - Math.Max(0, energy)) * rules.FatigueFactorPermille / rules.FatigueBelowEnergy;
    }

    /// <summary>Drinking riders crash more: 1000 sober, up to 1000 + <see cref="CrashRules.AlcoholFactorPermille"/> at full alcohol.</summary>
    public static int AlcoholFactor(CrashRules rules, int alcohol) => 1000 + Math.Clamp(alcohol, 0, 1000) * rules.AlcoholFactorPermille / 1000;

    /// <summary>Share of crashes that are serious (permille).</summary>
    public static int SeriousPermille(CrashRules rules, CrashCause cause, bool jump, int difficulty, int skill, bool protectedRider = false)
    {
        int share = rules.SeriousBasePermille;
        if (jump) share += rules.SeriousJumpPermille;
        if (cause == CrashCause.Collision) share += rules.SeriousCollisionPermille;
        share += Math.Max(0, difficulty - skill) * rules.SeriousPer100OverSkillPermille / 100;
        if (protectedRider) share = share * rules.ProtectedSeriousPermille / 1000;
        return Math.Clamp(share, 0, 1000);
    }

    /// <summary>
    /// The trail's crashes per 10 000 runs over today and the last closed days, taken over at least
    /// <see cref="CrashRules.RateMinRuns"/> runs.
    /// </summary>
    public static int TrailRatePer10k(CrashRules rules, Way trail)
    {
        long runs = trail.Stats.RunsToday, crashes = trail.Stats.CrashesToday;
        foreach (var day in trail.Stats.History)
        {
            runs += day.Runs;
            crashes += day.Crashes;
        }
        return (int)(crashes * 10_000 / Math.Max(runs, rules.RateMinRuns));
    }

    /// <summary>The crashes per 10 000 runs guests accept on a trail of this rating.</summary>
    public static int ExpectedPer10k(CrashRules rules, TrailRating rating) => rules.ExpectedCrashesPer10kRunsByRating.For(rating);

    /// <summary>Safety (permille) a review loses for a trail crashing at <paramref name="ratePer10k"/> (0 at or below expected).</summary>
    public static int RateSafetyLoss(CrashRules rules, int ratePer10k, TrailRating rating) =>
        (int)Math.Min(rules.MaxRateSafetyLoss, Math.Max(0L, ratePer10k - ExpectedPer10k(rules, rating)) * rules.SafetyLossPerExtraCrashPer10k);

    /// <summary>
    /// Safety a guest's review loses for the worst trail they rode this visit whose crash rate is above what its rating
    /// allows (badly placed crossings, a wrong rating, worn features). 0 without crashes.
    /// </summary>
    public static int UnsafeTrailLoss(WorldState state, WayNetwork network, Guest guest)
    {
        var rules = state.CrashRules;
        if (!rules.Enabled) return 0;
        int loss = 0;
        foreach (int id in guest.TrailsRidden)
            if (network.FindWay(id) is { } trail && network.TryGetGeometry(id, out var geometry))
                loss = Math.Max(loss, RateSafetyLoss(rules, TrailRatePer10k(rules, trail), geometry.Rating));
        return loss;
    }

    /// <summary>True with the given chance in parts per billion.</summary>
    public static bool Roll(Core.SimRandom rng, long ppb) => ppb > 0 && rng.NextInt(Billion) < ppb;

    private static long Clamp(long ppb) => Math.Clamp(ppb, 0, Billion);
}
