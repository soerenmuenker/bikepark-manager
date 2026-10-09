using Bikepark.Sim.Core;
using Bikepark.Sim.State;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Reputation;

/// <summary>A visit judged on each aspect, 0..1000.</summary>
public readonly record struct ReviewScores(int Fun, int Safety, int Smoothness, int Challenge, int Jumps, int Variety, int Value);

/// <summary>Turns a guest's visit into a review (pure; no randomness).</summary>
public static class ReviewMath
{
    public static SkillGroup GroupOf(ReputationRules rules, int skill) =>
        skill >= rules.ExpertFromSkill ? SkillGroup.Expert
        : skill >= rules.IntermediateFromSkill ? SkillGroup.Intermediate
        : SkillGroup.Beginner;

    /// <summary>Feature kinds riders get air on (their fun counts for the jump aspect).</summary>
    public static bool IsJump(FeatureKind kind) => kind is FeatureKind.Table or FeatureKind.Double or FeatureKind.Kicker or FeatureKind.Drop;

    /// <summary>The visit so far, judged on each aspect against what the guest's group looks for.</summary>
    public static ReviewScores Score(ReputationRules rules, Guest guest, long tick)
    {
        if (guest.RunsCompleted == 0)
            return new ReviewScores(0, Safe(guest, 1000), 0, 0, 0, 0, guest.PaidEntryCents == 0 ? 1000 : 0);
        var values = rules.Values(GroupOf(rules, guest.Skill));
        int runFun = (int)(guest.VisitFunSum / guest.RunsCompleted);
        int fun = (Stretch(rules, runFun) * 2 + guest.Happiness) / 3;
        int safety = Safe(guest, 1000 - guest.ScaredRuns * 1000 / guest.RunsCompleted);
        long visitSeconds = Math.Max(30, tick - guest.ArrivedTick) * 60L;
        long waitedSeconds = guest.QueueMinutes * 60L + (long)guest.HeldUpSeconds * rules.HeldUpWeight;
        int smoothness = (int)Math.Clamp(1000 - waitedSeconds * rules.WaitPenaltyPermille / visitSeconds, 0, 1000);
        int challenge = Math.Clamp(guest.HardestDifficulty * 1000 / (values?.ChallengeTarget ?? 500), 0, 1000);
        int jumps = guest.JumpCount == 0 ? 0 : Stretch(rules, (int)(guest.JumpFunSum / guest.JumpCount));
        int variety = Math.Min(1000, guest.TrailsRidden.Count * 1000 / rules.VarietyFullAt);
        int value = guest.PaidEntryCents <= 0 ? 1000
            : (int)Math.Min(1000, guest.RunsCompleted * rules.FairCentsPerRun * 1000 / guest.PaidEntryCents);
        return new ReviewScores(fun, safety, smoothness, challenge, jumps, variety, value);
    }

    /// <summary>Stars in tenths (10..50) a group gives for these scores.</summary>
    public static int Stars(ReputationRules rules, SkillGroup group, ReviewScores s)
    {
        if (rules.Values(group) is not { } v) return 30;
        long score = ((long)v.Fun * s.Fun + (long)v.Safety * s.Safety + (long)v.Smoothness * s.Smoothness
                      + (long)v.Challenge * s.Challenge + (long)v.Jumps * s.Jumps + (long)v.Variety * s.Variety + (long)v.Value * s.Value) / 1000;
        return 10 + (int)(Math.Clamp(score, 0, 1000) * 40 / 1000);
    }

    /// <summary>The guest's review, as of <paramref name="tick"/>.</summary>
    public static Review Write(ReputationRules rules, Guest guest, long tick)
    {
        var group = GroupOf(rules, guest.Skill);
        var s = Score(rules, guest, tick);
        int stars = guest.RunsCompleted == 0 ? 10 : Stars(rules, group, s);
        return new Review(GameTime.Day(tick), guest.Id, group, stars, s.Fun, s.Safety, s.Smoothness, s.Challenge, s.Jumps, s.Variety,
            guest.IsInfluencer, s.Value);
    }

    /// <summary>
    /// Crashes override how safe the day felt: nothing is safe about a helicopter ride, a minor crash caps it low, and
    /// each crash seen on the trail ahead costs a bit.
    /// </summary>
    private static int Safe(Guest guest, int safety) => guest.Injury switch
    {
        Safety.InjurySeverity.Serious => 0,
        Safety.InjurySeverity.Minor => Math.Min(safety, 200),
        _ => Math.Max(0, safety - guest.CrashesSeen * 150),
    };

    private static int Stretch(ReputationRules rules, int fun) =>
        Math.Clamp((fun - rules.FunFloor) * 1000 / (rules.FunCeiling - rules.FunFloor), 0, 1000);
}

/// <summary>Rating and visitor demand, derived from <see cref="ReputationState"/>.</summary>
public static class ReputationMath
{
    private static readonly string[] InfluencerNames =
        ["@SendItSam", "@LoamLena", "@BermBaron", "@DropInDani", "@FlowFinn", "@RootsRiley", "@ShredShira", "@GnarGustav"];

    /// <summary>An influencer's handle (derived from their guest id).</summary>
    public static string InfluencerName(int guestId) => InfluencerNames[guestId % InfluencerNames.Length];

    /// <summary>Overall rating in tenths of a star, or null while there are too few reviews.</summary>
    public static int? RatingTenths(WorldState state)
    {
        var rep = state.Reputation;
        if (rep.TotalReviews < state.ReputationRules.MinReviewsForRating || rep.Reviews.Count == 0) return null;
        long sum = rep.Reviews.Sum(r => (long)r.StarsTenths);
        return (int)((sum * 2 + rep.Reviews.Count) / (rep.Reviews.Count * 2L)); // rounded
    }

    /// <summary>A group's rating in tenths of a star, or null while it has too few reviews.</summary>
    public static int? RatingTenths(WorldState state, SkillGroup group)
    {
        var rep = state.Reputation;
        if (rep.ReviewsOf(group) < state.ReputationRules.MinReviewsForRating) return null;
        long sum = 0;
        int count = 0;
        foreach (var r in rep.Reviews)
            if (r.Group == group)
            {
                sum += r.StarsTenths;
                count++;
            }
        return count == 0 ? null : (int)((sum * 2 + count) / (count * 2)); // rounded
    }

    /// <summary>The reputation part of the arrival rate, in permille, and its parts.</summary>
    public static (int Total, int Visibility, int Rating, int Influencer) Demand(WorldState state)
    {
        var rules = state.ReputationRules;
        if (!rules.Enabled) return (1000, 1000, 1000, 0);
        int visibility = ReputationRules.Evaluate(rules.VisibilityCurve, state.Reputation.TotalReviews);
        int rating = RatingTenths(state) is { } stars ? ReputationRules.Evaluate(rules.RatingCurve, stars) : 1000;
        int influencer = InfluencerEffect(state, GameTime.Day(state.Tick));
        long total = (long)visibility * rating / 1000 * (1000 + influencer) / 1000;
        return ((int)Math.Clamp(total, rules.DemandMinPermille, rules.DemandMaxPermille), visibility, rating, influencer);
    }

    public static int DemandPermille(WorldState state) => Demand(state).Total;

    /// <summary>The active post (the newest one still in effect), if any.</summary>
    public static InfluencerPost? ActivePost(WorldState state, long day) =>
        state.Reputation.Posts.LastOrDefault(p => p.Day <= day && day < p.EndsDay);

    /// <summary>The demand change from the active post today: full on its day, fading linearly to 0 by its end.</summary>
    public static int InfluencerEffect(WorldState state, long day)
    {
        if (ActivePost(state, day) is not { } post) return 0;
        long length = Math.Max(1, post.EndsDay - post.Day);
        return (int)(post.EffectPermille * (post.EndsDay - day) / length);
    }

    /// <summary>The demand change a post of these stars brings on its day.</summary>
    public static int PostEffect(ReputationRules rules, int starsTenths)
    {
        int range = starsTenths >= rules.InfluencerNeutralTenths ? 50 - rules.InfluencerNeutralTenths : rules.InfluencerNeutralTenths - 10;
        return range == 0 ? 0 : (starsTenths - rules.InfluencerNeutralTenths) * rules.InfluencerPeakPermille / range;
    }

    /// <summary>Adds a review to the window (dropping the group's oldest beyond the window size) and the counts.</summary>
    public static void Add(WorldState state, Review review)
    {
        var rep = state.Reputation;
        rep.Reviews.Add(review);
        rep.TotalReviews++;
        rep.ReviewsToday++;
        switch (review.Group)
        {
            case SkillGroup.Beginner: rep.BeginnerReviews++; break;
            case SkillGroup.Intermediate: rep.IntermediateReviews++; break;
            default: rep.ExpertReviews++; break;
        }
        int inWindow = rep.Reviews.Count(r => r.Group == review.Group);
        if (inWindow > state.ReputationRules.WindowSize)
            rep.Reviews.RemoveAt(rep.Reviews.FindIndex(r => r.Group == review.Group));
    }
}

/// <summary>Park XP and level, derived from the park as it is now (built trails, their ratings and features, recent visitors).</summary>
public static class ParkProgress
{
    public sealed record XpBreakdown(
        long TrailMeters, int TrailXp,
        int Ratings, int RatingXp,
        int FeatureKinds, int FeatureXp,
        int AverageVisitors, int VisitorXp)
    {
        public int Total => TrailXp + RatingXp + FeatureXp + VisitorXp;
    }

    public static XpBreakdown Xp(WorldState state, WayNetwork network)
    {
        var rules = state.ReputationRules;
        long lengthCm = 0;
        var ratings = new List<TrailRating>();
        var kinds = new List<FeatureKind>();
        foreach (var trail in network.Trails)
        {
            if (!network.TryGetGeometry(trail.Id, out var geometry)) continue;
            lengthCm += geometry.LengthCm;
            if (!ratings.Contains(geometry.Rating)) ratings.Add(geometry.Rating);
            foreach (var feature in network.FeaturesOn(trail.Id))
                if (feature.Feature.Built && !kinds.Contains(feature.Type.Kind))
                    kinds.Add(feature.Type.Kind);
        }
        var visitors = state.Reputation.DailyVisitors;
        int average = visitors.Count == 0 ? 0 : (int)(visitors.Sum(v => (long)v) / visitors.Count);
        return new XpBreakdown(
            lengthCm / 100, (int)(lengthCm * rules.XpPerTrailKm / 100_000),
            ratings.Count, ratings.Count * rules.XpPerRating,
            kinds.Count, kinds.Count * rules.XpPerFeatureKind,
            average, average * rules.XpPerVisitor);
    }

    /// <summary>The park's level right now (derived from its XP).</summary>
    public static int CurrentLevel(WorldState state, WayNetwork network) => Level(state.ReputationRules, Xp(state, network).Total);

    /// <summary>The level an amount of XP reaches (0 below the first threshold).</summary>
    public static int Level(ReputationRules rules, int xp)
    {
        int level = 0;
        while (level < rules.LevelXp.Count && xp >= rules.LevelXp[level])
            level++;
        return level;
    }

    /// <summary>XP needed for the next level, or null at the top.</summary>
    public static int? NextLevelXp(ReputationRules rules, int level) => level < rules.LevelXp.Count ? rules.LevelXp[level] : null;
}
