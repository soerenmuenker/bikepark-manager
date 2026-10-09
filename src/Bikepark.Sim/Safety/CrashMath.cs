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
        return Clamp(ppb * WetFactor(rules, wetnessPermille) / 1000 * FatigueFactor(rules, guest.Energy) / 1000);
    }

    /// <summary>Chance of crashing on a 10 m segment of rough or steep trail.</summary>
    public static long TerrainChancePpb(CrashRules rules, Guest guest, WaySegment segment, int wetnessPermille)
    {
        long ppb = rules.TerrainBasePpm * 1000L * SkillFactor(rules, segment.TerrainDifficulty, guest.Skill) / 1000;
        return Clamp(ppb * WetFactor(rules, wetnessPermille) / 1000 * FatigueFactor(rules, guest.Energy) / 1000);
    }

    /// <summary>Chance of colliding at a crossing while someone on the other way is close to it.</summary>
    public static long CollisionChancePpb(CrashRules rules) => Clamp(rules.CollisionPpm * 1000L);

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

    /// <summary>Share of crashes that are serious (permille).</summary>
    public static int SeriousPermille(CrashRules rules, CrashCause cause, bool jump, int difficulty, int skill)
    {
        int share = rules.SeriousBasePermille;
        if (jump) share += rules.SeriousJumpPermille;
        if (cause == CrashCause.Collision) share += rules.SeriousCollisionPermille;
        share += Math.Max(0, difficulty - skill) * rules.SeriousPer100OverSkillPermille / 100;
        return Math.Clamp(share, 0, 1000);
    }

    /// <summary>The daily insurance premium for the accidents in the history.</summary>
    public static long Premium(CrashRules rules, SafetyState safety) =>
        rules.InsuranceBaseCents
        + safety.History.Sum(d => d.Minor * rules.InsurancePerMinorCents + d.Serious * rules.InsurancePerSeriousCents);

    /// <summary>True with the given chance in parts per billion.</summary>
    public static bool Roll(Core.SimRandom rng, long ppb) => ppb > 0 && rng.NextInt(Billion) < ppb;

    private static long Clamp(long ppb) => Math.Clamp(ppb, 0, Billion);
}
