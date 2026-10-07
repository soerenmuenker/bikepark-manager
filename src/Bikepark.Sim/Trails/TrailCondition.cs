using Bikepark.Sim.State;

namespace Bikepark.Sim.Trails;

/// <summary>
/// Wear tuning, loaded from the scenario and saved with the game. Only trail features wear (the trail itself does not).
/// Condition is in millionths per feature (1,000,000 = perfect); thresholds are in permille.
/// <see cref="WearPerPass"/> 0 (the default) turns wear off.
/// </summary>
public sealed class WearRules
{
    /// <summary>Condition (millionths) a rider's pass takes off a feature of difficulty 0 on dry ground.</summary>
    public int WearPerPass { get; set; }

    /// <summary>Extra wear on fully wet ground (2000 = three times the wear), scaled by the wetness.</summary>
    public int WetWearPermille { get; set; } = 2000;

    /// <summary>Below this condition a feature is slower and less fun, worst at 0.</summary>
    public int RoughBelowPermille { get; set; } = 700;
    public int WornSpeedLossPermille { get; set; } = 300;
    public int WornFunLoss { get; set; } = 400;

    /// <summary>Below this condition the player gets a warning to send the crew. At 0 the trail closes by itself.</summary>
    public int WarnBelowPermille { get; set; } = 200;

    public bool Enabled => WearPerPass > 0;

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (WearPerPass is < 0 or > 1_000_000) errors.Add("wearRules.wearPerPass must be within 0..1000000");
        if (WetWearPermille is < 0 or > 20_000) errors.Add("wearRules.wetWearPermille must be within 0..20000");
        if (RoughBelowPermille is < 1 or > 1000 || WarnBelowPermille is < 1 or > 1000)
            errors.Add("wearRules: thresholds must be within 1..1000");
        if (WornSpeedLossPermille is < 0 or > 900) errors.Add("wearRules.wornSpeedLossPermille must be within 0..900");
        if (WornFunLoss is < 0 or > 1000) errors.Add("wearRules.wornFunLoss must be within 0..1000");
        return errors;
    }
}

/// <summary>Reading and changing a feature's condition (<see cref="TrailFeature.Condition"/>), and its effect on riding.</summary>
public static class TrailCondition
{
    public const int Perfect = 1_000_000;

    public static int Permille(TrailFeature feature) => feature.Condition / 1000;

    /// <summary>The worst built feature of a trail in permille (1000 if it has none).</summary>
    public static int WorstPermille(Way way)
    {
        int worst = Perfect;
        foreach (var feature in way.Features)
            if (feature.Built)
                worst = Math.Min(worst, feature.Condition);
        return worst / 1000;
    }

    /// <summary>Wear of one rider passing a feature: harder features and wet ground wear more.</summary>
    public static int PassWear(WorldState state, TrailFeatureType type)
    {
        var rules = state.WearRules;
        long wear = (long)rules.WearPerPass * (1000 + Math.Clamp(type.Difficulty, 0, 1000)) / 1000;
        wear = wear * (1000 + (long)rules.WetWearPermille * state.Weather.WetnessPermille / 1000) / 1000;
        return (int)wear;
    }

    public static void Wear(TrailFeature feature, int amount)
    {
        if (amount > 0)
            feature.Condition = Math.Max(0, feature.Condition - amount);
    }

    /// <summary>Repaired: perfect again, and the warning can be raised again.</summary>
    public static void Restore(TrailFeature feature)
    {
        feature.Condition = Perfect;
        feature.Warned = false;
    }

    /// <summary>How much slower riders are on a feature in this condition, permille (0 above the rough threshold).</summary>
    public static int SpeedLossPermille(WearRules rules, int conditionPermille) =>
        conditionPermille >= rules.RoughBelowPermille ? 0
            : (rules.RoughBelowPermille - conditionPermille) * rules.WornSpeedLossPermille / rules.RoughBelowPermille;

    /// <summary>Fun lost on a feature in this condition (0..1000 scale).</summary>
    public static int FunLoss(WearRules rules, int conditionPermille) =>
        conditionPermille >= rules.RoughBelowPermille ? 0
            : (rules.RoughBelowPermille - conditionPermille) * rules.WornFunLoss / rules.RoughBelowPermille;
}
