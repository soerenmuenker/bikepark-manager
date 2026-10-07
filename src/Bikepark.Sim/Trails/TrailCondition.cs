using Bikepark.Sim.State;

namespace Bikepark.Sim.Trails;

/// <summary>
/// Wear tuning (trails only), loaded from the scenario and saved with the game. Condition is in millionths per segment
/// (1,000,000 = perfect); thresholds are in permille. <see cref="WearPerPass"/> 0 (the default) turns wear off.
/// </summary>
public sealed class WearRules
{
    /// <summary>Condition (millionths) a rider's pass takes off a segment of difficulty 0 on dry ground.</summary>
    public int WearPerPass { get; set; }

    /// <summary>Extra wear on fully wet ground (2000 = three times the wear), scaled by the wetness.</summary>
    public int WetWearPermille { get; set; } = 2000;

    /// <summary>Below this condition riding gets slower and less fun, worst at 0.</summary>
    public int RoughBelowPermille { get; set; } = 700;
    public int WornSpeedLossPermille { get; set; } = 300;
    public int WornFunLoss { get; set; } = 400;

    /// <summary>A trail closes when a segment drops below this; it reopens once repaired.</summary>
    public int CloseBelowPermille { get; set; } = 250;

    /// <summary>Trails set to maintain get a repair job when a segment drops below this.</summary>
    public int MaintainBelowPermille { get; set; } = 600;

    public bool Enabled => WearPerPass > 0;

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (WearPerPass is < 0 or > 1_000_000) errors.Add("wearRules.wearPerPass must be within 0..1000000");
        if (WetWearPermille is < 0 or > 20_000) errors.Add("wearRules.wetWearPermille must be within 0..20000");
        if (RoughBelowPermille is < 1 or > 1000 || CloseBelowPermille is < 0 or > 1000 || MaintainBelowPermille is < 0 or > 1000)
            errors.Add("wearRules: thresholds must be within 0..1000 (rough >= 1)");
        if (WornSpeedLossPermille is < 0 or > 900) errors.Add("wearRules.wornSpeedLossPermille must be within 0..900");
        if (WornFunLoss is < 0 or > 1000) errors.Add("wearRules.wornFunLoss must be within 0..1000");
        return errors;
    }
}

/// <summary>Reading and changing a trail's condition (<see cref="Way.Condition"/>), and its effect on riding.</summary>
public static class TrailCondition
{
    public const int Perfect = 1_000_000;

    /// <summary>A segment's condition in millionths.</summary>
    public static int Get(Way way, int segment) =>
        segment >= 0 && segment < way.Condition.Count ? way.Condition[segment] : Perfect;

    public static int Permille(Way way, int segment) => Get(way, segment) / 1000;

    /// <summary>The worst segment's condition in permille (segments as derived now).</summary>
    public static int WorstPermille(Way way, int segments)
    {
        int worst = Perfect;
        for (int i = 0; i < Math.Min(segments, way.Condition.Count); i++)
            worst = Math.Min(worst, way.Condition[i]);
        return worst / 1000;
    }

    public static int AveragePermille(Way way, int segments)
    {
        if (segments <= 0) return 1000;
        long sum = 0;
        for (int i = 0; i < segments; i++)
            sum += Get(way, i);
        return (int)(sum / segments / 1000);
    }

    /// <summary>Wear of one rider passing a segment: harder segments (braking, landings) and wet ground wear more.</summary>
    public static int PassWear(WorldState state, WaySegment segment)
    {
        var rules = state.WearRules;
        long wear = (long)rules.WearPerPass * (1000 + Math.Clamp(segment.Difficulty, 0, 1000)) / 1000;
        wear = wear * (1000 + (long)rules.WetWearPermille * state.Weather.WetnessPermille / 1000) / 1000;
        return (int)wear;
    }

    public static void Wear(Way way, int segment, int amount)
    {
        if (amount <= 0 || segment < 0) return;
        while (way.Condition.Count <= segment)
            way.Condition.Add(Perfect);
        way.Condition[segment] = Math.Max(0, way.Condition[segment] - amount);
    }

    /// <summary>Repaired: every segment is perfect again.</summary>
    public static void Restore(Way way) => way.Condition.Clear();

    /// <summary>How much slower riders are on a segment in this condition, permille (0 above the rough threshold).</summary>
    public static int SpeedLossPermille(WearRules rules, int conditionPermille) =>
        conditionPermille >= rules.RoughBelowPermille ? 0
            : (rules.RoughBelowPermille - conditionPermille) * rules.WornSpeedLossPermille / rules.RoughBelowPermille;

    /// <summary>Fun lost on a segment in this condition (0..1000 scale).</summary>
    public static int FunLoss(WearRules rules, int conditionPermille) =>
        conditionPermille >= rules.RoughBelowPermille ? 0
            : (rules.RoughBelowPermille - conditionPermille) * rules.WornFunLoss / rules.RoughBelowPermille;
}
