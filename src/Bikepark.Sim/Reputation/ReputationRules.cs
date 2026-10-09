using System.Text.Json.Serialization;

namespace Bikepark.Sim.Reputation;

/// <summary>Riders grouped by <see cref="State.Guest.Skill"/>; each group rates the park by what it values.</summary>
public enum SkillGroup : byte
{
    Beginner = 0,
    Intermediate = 1,
    Expert = 2,
}

/// <summary>One point of a piecewise-linear curve (held flat before the first and after the last point).</summary>
public sealed record CurvePoint(int X, int Permille);

/// <summary>
/// What one skill group values: weights in permille (summing to 1000) over the review aspects (see
/// <see cref="ReviewScores"/>), the trail difficulty it finds challenging, and a line for the UI.
/// </summary>
public sealed class GroupValues
{
    public SkillGroup Group { get; set; }
    public string Name { get; set; } = "";

    /// <summary>What the group looks for, in words (shown in the reputation menu).</summary>
    public string Values { get; set; } = "";

    public int Fun { get; set; }
    public int Safety { get; set; }
    public int Smoothness { get; set; }
    public int Challenge { get; set; }
    public int Jumps { get; set; }
    public int Variety { get; set; }

    /// <summary>Value for money: riding they got for the entry fee they paid.</summary>
    public int Value { get; set; }

    /// <summary>The hardest trail difficulty (0..1000) a visit needs for full challenge marks.</summary>
    public int ChallengeTarget { get; set; } = 500;

    [JsonIgnore]
    public int WeightSum => Fun + Safety + Smoothness + Challenge + Jumps + Variety + Value;
}

/// <summary>
/// Reviews, rating, visitor demand, influencers and park XP; loaded from the scenario and saved with the game. Off by
/// default (no reviews, demand 100 %, no influencers), so older saves and scenarios behave as before; XP and the level are
/// derived either way (no level thresholds = level 0).
/// </summary>
public sealed class ReputationRules
{
    public bool Enabled { get; set; }

    /// <summary>Skill (0..1000) from which a rider counts as intermediate / expert.</summary>
    public int IntermediateFromSkill { get; set; } = 350;
    public int ExpertFromSkill { get; set; } = 650;

    /// <summary>Beginner, intermediate, expert (in this order).</summary>
    public List<GroupValues> Groups { get; set; } = [];

    // ---- Reviews ----

    /// <summary>Chance that a guest who rode at least one run writes a review on leaving.</summary>
    public int ReviewChancePermille { get; set; } = 600;

    /// <summary>The rating is the average of the last this-many reviews per group.</summary>
    public int WindowSize { get; set; } = 200;

    /// <summary>All-time reviews (overall or per group) before a rating is shown and counts for demand.</summary>
    public int MinReviewsForRating { get; set; } = 10;

    /// <summary>A run on a trail this much harder than the rider's skill counts as scary (hurts safety).</summary>
    public int ScaredMargin { get; set; } = 150;

    /// <summary>Run fun (0..1000) mapped to 0 and full marks for the fun and jump aspects.</summary>
    public int FunFloor { get; set; } = 250;
    public int FunCeiling { get; set; } = 800;

    /// <summary>How much a second held up behind a slower rider counts against smoothness, compared to a second in a queue.</summary>
    public int HeldUpWeight { get; set; } = 1;

    /// <summary>Waiting share of the visit (permille) times this / 1000 is lost from smoothness (2000: half the visit spent waiting = 0).</summary>
    public int WaitPenaltyPermille { get; set; } = 2000;

    /// <summary>Distinct trails ridden in one visit for full variety marks.</summary>
    public int VarietyFullAt { get; set; } = 3;

    /// <summary>
    /// What guests think one run is worth: full value marks when runs x this covers the entry fee paid (a free day is
    /// always full value; e.g. 150: ten runs make a 15 € ticket worth it, five runs give half marks).
    /// </summary>
    public long FairCentsPerRun { get; set; } = 150;

    // ---- Demand ----

    /// <summary>Visibility by all-time review count (X = reviews), permille.</summary>
    public List<CurvePoint> VisibilityCurve { get; set; } = [];

    /// <summary>Rating factor by overall stars (X = tenths of a star, 10..50), permille; 1000 while there is no rating.</summary>
    public List<CurvePoint> RatingCurve { get; set; } = [];

    public int DemandMinPermille { get; set; } = 300;
    public int DemandMaxPermille { get; set; } = 1500;

    // ---- Influencers ----

    /// <summary>Chance per open day that an influencer comes (from <see cref="InfluencerFirstDay"/>, at most every <see cref="InfluencerMinGapDays"/>).</summary>
    public int InfluencerChancePermille { get; set; }
    public int InfluencerFirstDay { get; set; } = 3;
    public int InfluencerMinGapDays { get; set; } = 7;
    public int InfluencerSkill { get; set; } = 750;
    public int InfluencerStayMinutes { get; set; } = 300;

    /// <summary>Demand change of a 5-star post on its day (a 1-star post: the same, negative), fading to 0 over <see cref="InfluencerEffectDays"/>.</summary>
    public int InfluencerPeakPermille { get; set; } = 400;

    /// <summary>A post of this many tenths of a star is neutral.</summary>
    public int InfluencerNeutralTenths { get; set; } = 30;
    public int InfluencerEffectDays { get; set; } = 5;

    // ---- XP and level ----

    public int XpPerTrailKm { get; set; } = 100;

    /// <summary>Per distinct trail rating (green, blue, red, black) among built trails.</summary>
    public int XpPerRating { get; set; } = 50;

    /// <summary>Per distinct kind of built feature (berm, rollers, table, ...).</summary>
    public int XpPerFeatureKind { get; set; } = 25;

    /// <summary>XP per average daily visitor over the last <see cref="VisitorDays"/> days.</summary>
    public int XpPerVisitor { get; set; } = 1;
    public int VisitorDays { get; set; } = 7;

    /// <summary>XP needed for levels 1, 2, ... (ascending; up to <see cref="MaxLevel"/> entries).</summary>
    public List<int> LevelXp { get; set; } = [];

    public const int MaxLevel = 20;

    public GroupValues? Values(SkillGroup group) => Groups.FirstOrDefault(g => g.Group == group);

    public List<string> Validate()
    {
        var errors = new List<string>();
        const string p = "reputationRules";
        if (IntermediateFromSkill is < 0 or > 1000 || ExpertFromSkill is < 0 or > 1000 || ExpertFromSkill <= IntermediateFromSkill)
            errors.Add($"{p}: skill bounds must satisfy 0 <= intermediateFromSkill < expertFromSkill <= 1000");
        if (Enabled && (Groups.Count != 3 || Groups[0].Group != SkillGroup.Beginner || Groups[1].Group != SkillGroup.Intermediate
                        || Groups[2].Group != SkillGroup.Expert))
            errors.Add($"{p}.groups must list beginner, intermediate and expert in this order");
        foreach (var g in Groups)
        {
            if (g.WeightSum != 1000) errors.Add($"{p}.groups[{g.Group}]: weights must sum to 1000");
            if (new[] { g.Fun, g.Safety, g.Smoothness, g.Challenge, g.Jumps, g.Variety, g.Value }.Any(w => w < 0))
                errors.Add($"{p}.groups[{g.Group}]: weights must be >= 0");
            if (g.ChallengeTarget is < 1 or > 1000) errors.Add($"{p}.groups[{g.Group}].challengeTarget must be within 1..1000");
        }
        if (ReviewChancePermille is < 0 or > 1000) errors.Add($"{p}.reviewChancePermille must be within 0..1000");
        if (WindowSize is < 1 or > 10_000) errors.Add($"{p}.windowSize must be within 1..10000");
        if (MinReviewsForRating < 1) errors.Add($"{p}.minReviewsForRating must be >= 1");
        if (ScaredMargin is < 0 or > 1000) errors.Add($"{p}.scaredMargin must be within 0..1000");
        if (FunFloor < 0 || FunCeiling > 1000 || FunCeiling <= FunFloor) errors.Add($"{p}: 0 <= funFloor < funCeiling <= 1000");
        if (HeldUpWeight is < 0 or > 100) errors.Add($"{p}.heldUpWeight must be within 0..100");
        if (WaitPenaltyPermille is < 0 or > 100_000) errors.Add($"{p}.waitPenaltyPermille must be within 0..100000");
        if (VarietyFullAt < 1) errors.Add($"{p}.varietyFullAt must be >= 1");
        if (FairCentsPerRun is < 1 or > 100_000) errors.Add($"{p}.fairCentsPerRun must be within 1..100000");
        CheckCurve(errors, VisibilityCurve, $"{p}.visibilityCurve");
        CheckCurve(errors, RatingCurve, $"{p}.ratingCurve");
        if (DemandMinPermille < 0 || DemandMaxPermille < DemandMinPermille || DemandMaxPermille > 10_000)
            errors.Add($"{p}: 0 <= demandMinPermille <= demandMaxPermille <= 10000");
        if (InfluencerChancePermille is < 0 or > 1000) errors.Add($"{p}.influencerChancePermille must be within 0..1000");
        if (InfluencerFirstDay < 0 || InfluencerMinGapDays < 1) errors.Add($"{p}: influencerFirstDay >= 0, influencerMinGapDays >= 1");
        if (InfluencerSkill is < 0 or > 1000) errors.Add($"{p}.influencerSkill must be within 0..1000");
        if (InfluencerStayMinutes is < 1 or > 1440) errors.Add($"{p}.influencerStayMinutes must be within 1..1440");
        if (InfluencerPeakPermille is < 0 or > 1000) errors.Add($"{p}.influencerPeakPermille must be within 0..1000");
        if (InfluencerNeutralTenths is < 10 or > 50) errors.Add($"{p}.influencerNeutralTenths must be within 10..50");
        if (InfluencerEffectDays < 1) errors.Add($"{p}.influencerEffectDays must be >= 1");
        if (XpPerTrailKm < 0 || XpPerRating < 0 || XpPerFeatureKind < 0 || XpPerVisitor < 0) errors.Add($"{p}: xp values must be >= 0");
        if (VisitorDays < 1) errors.Add($"{p}.visitorDays must be >= 1");
        if (LevelXp.Count > MaxLevel) errors.Add($"{p}.levelXp: at most {MaxLevel} levels");
        for (int i = 0; i < LevelXp.Count; i++)
            if (LevelXp[i] <= (i == 0 ? 0 : LevelXp[i - 1]))
                errors.Add($"{p}.levelXp must be positive and ascending");
        return errors;
    }

    private static void CheckCurve(List<string> errors, List<CurvePoint> curve, string name)
    {
        for (int i = 0; i < curve.Count; i++)
        {
            if (curve[i].Permille is < 0 or > 10_000) errors.Add($"{name}: permille must be within 0..10000");
            if (i > 0 && curve[i].X <= curve[i - 1].X) errors.Add($"{name}: x must increase");
        }
    }

    /// <summary>A curve's value at <paramref name="x"/> (<paramref name="empty"/> without points).</summary>
    public static int Evaluate(List<CurvePoint> curve, long x, int empty = 1000)
    {
        if (curve.Count == 0) return empty;
        if (x <= curve[0].X) return curve[0].Permille;
        for (int i = 1; i < curve.Count; i++)
        {
            var (a, b) = (curve[i - 1], curve[i]);
            if (x <= b.X)
                return (int)(a.Permille + (b.Permille - a.Permille) * (x - a.X) / (b.X - a.X));
        }
        return curve[^1].Permille;
    }
}
