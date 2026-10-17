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
    public static bool IsJump(FeatureKind kind) =>
        kind is FeatureKind.Table or FeatureKind.Double or FeatureKind.Kicker or FeatureKind.Drop or FeatureKind.LongJump or FeatureKind.BigDrop;

    /// <summary>
    /// The visit so far, judged on each aspect against what the guest's group looks for. <paramref name="unsafeTrailLoss"/>:
    /// safety lost to a trail that crashes more than its rating allows (<see cref="Safety.CrashMath.UnsafeTrailLoss"/>).
    /// </summary>
    public static ReviewScores Score(ReputationRules rules, Guest guest, long tick, int unsafeTrailLoss = 0)
    {
        if (guest.RunsCompleted == 0)
            return new ReviewScores(0, Safe(guest, 1000), 0, 0, 0, 0, guest.PaidEntryCents + guest.OverpaidCents == 0 ? 1000 : 0);
        var values = rules.Values(GroupOf(rules, guest.Skill));
        int runFun = (int)(guest.VisitFunSum / guest.RunsCompleted);
        int fun = (Stretch(rules, runFun) * 2 + guest.Happiness) / 3;
        int safety = Safe(guest, Math.Max(0, 1000 - guest.ScaredRuns * 1000 / guest.RunsCompleted - unsafeTrailLoss));
        long visitSeconds = Math.Max(30, tick - guest.ArrivedTick) * 60L;
        long waitedSeconds = guest.QueueMinutes * 60L + (long)guest.HeldUpSeconds * rules.HeldUpWeight;
        int smoothness = (int)Math.Clamp(1000 - waitedSeconds * rules.WaitPenaltyPermille / visitSeconds, 0, 1000);
        int challenge = Math.Clamp(guest.HardestDifficulty * 1000 / (values?.ChallengeTarget ?? 500), 0, 1000);
        int jumps = guest.JumpCount == 0 ? 0 : Stretch(rules, (int)(guest.JumpFunSum / guest.JumpCount));
        int variety = Math.Min(1000, guest.TrailsRidden.Count * 1000 / rules.VarietyFullAt);
        // Overpriced food and drink count like paying more to get in.
        long paid = guest.PaidEntryCents + guest.OverpaidCents;
        int value = paid <= 0 ? 1000
            : (int)Math.Min(1000, guest.RunsCompleted * rules.FairCentsPerRun * 1000 / paid);
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

    public const int MaxTrails = 5, MaxFacilities = 5, MaxLifts = 3;

    /// <summary>The guest's review, as of <paramref name="tick"/>, with what the visit was (trails, buildings, lifts, money).</summary>
    public static Review Write(ReputationRules rules, Guest guest, long tick, int unsafeTrailLoss = 0,
        Events.GuestLeaveReason reason = default)
    {
        var group = GroupOf(rules, guest.Skill);
        var s = Score(rules, guest, tick, unsafeTrailLoss);
        int stars = guest.RunsCompleted == 0 ? 10 : Stars(rules, group, s);
        // Most ridden first; ties in the order first ridden.
        var trails = guest.TrailsRidden
            .Select((id, i) => (Id: id, Runs: i < guest.TrailRuns.Count ? guest.TrailRuns[i] : 0, Index: i))
            .OrderByDescending(t => t.Runs).ThenBy(t => t.Index)
            .Take(MaxTrails).Select(t => t.Id).ToList();
        return new Review(GameTime.Day(tick), guest.Id, group, stars, s.Fun, s.Safety, s.Smoothness, s.Challenge, s.Jumps, s.Variety,
            guest.IsInfluencer, s.Value)
        {
            LeftTick = tick,
            TrailIds = trails,
            FacilityIds = guest.FacilitiesUsed.Take(MaxFacilities).ToList(),
            LiftIds = guest.LiftsUsed.Take(MaxLifts).ToList(),
            SpentCents = Math.Max(0, guest.BroughtCents - guest.CashCents),
            Reason = reason,
            Crashed = guest.Injury != Safety.InjurySeverity.None,
        };
    }

    /// <summary>
    /// The average of each aspect (0..1000, rounded) over <paramref name="reviews"/>, and how many there were; all 0 for none.
    /// Use it on the whole window, one group, or the reviews that mention a trail, lift or building.
    /// </summary>
    public static (ReviewScores Average, int Count) AspectAverages(IEnumerable<Review> reviews)
    {
        long fun = 0, safety = 0, smooth = 0, challenge = 0, jumps = 0, variety = 0, value = 0;
        int n = 0;
        foreach (var r in reviews)
        {
            fun += r.Fun; safety += r.Safety; smooth += r.Smoothness; challenge += r.Challenge;
            jumps += r.Jumps; variety += r.Variety; value += r.Value;
            n++;
        }
        if (n == 0) return (default, 0);
        int Avg(long sum) => (int)((sum * 2 + n) / (2L * n));
        return (new ReviewScores(Avg(fun), Avg(safety), Avg(smooth), Avg(challenge), Avg(jumps), Avg(variety), Avg(value)), n);
    }

    /// <summary>Average stars (tenths, rounded) of <paramref name="reviews"/>, or null for none.</summary>
    public static int? AverageStars(IEnumerable<Review> reviews)
    {
        long sum = 0;
        int n = 0;
        foreach (var r in reviews)
        {
            sum += r.StarsTenths;
            n++;
        }
        return n == 0 ? null : (int)((sum * 2 + n) / (2L * n));
    }

    /// <summary>The reviews in the window that mention a trail, lift or building.</summary>
    public static IEnumerable<Review> Mentioning(ReputationState state, int trailId = 0, int liftId = 0, int facilityId = 0) =>
        state.Reviews.Where(r => (trailId != 0 && r.TrailIds.Contains(trailId)) || (liftId != 0 && r.LiftIds.Contains(liftId))
                                 || (facilityId != 0 && r.FacilityIds.Contains(facilityId)));

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
        long total = (long)visibility * rating / 1000 * (1000 + influencer) / 1000 * (1000 + state.Reputation.BuzzPermille) / 1000;
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

/// <summary>
/// Park XP and level, derived from the park as it is now: built trail length per rating, built features per rating
/// (their own difficulty band) and recent visitors.
/// </summary>
public static class ParkProgress
{
    public const int Ratings = 4;

    /// <summary>Per rating (index = <see cref="TrailRating"/>): metres of trail and built features, and the XP of each.</summary>
    public sealed record XpBreakdown(
        IReadOnlyList<long> TrailMeters, int TrailXp,
        IReadOnlyList<int> Features, int FeatureXp,
        int AverageVisitors, int VisitorXp)
    {
        public int Total => TrailXp + FeatureXp + VisitorXp;
        public long TotalTrailMeters => TrailMeters.Sum();
        public int TotalFeatures => Features.Sum();
    }

    public static XpBreakdown Xp(WorldState state, WayNetwork network)
    {
        var rules = state.ReputationRules;
        var lengthCm = new long[Ratings];
        var features = new int[Ratings];
        foreach (var trail in network.Trails)
        {
            if (!network.TryGetGeometry(trail.Id, out var geometry)) continue;
            lengthCm[(int)geometry.Rating] += geometry.LengthCm;
            foreach (var feature in network.FeaturesOn(trail.Id))
                if (feature.Feature.Built)
                    features[(int)feature.Type.Rating]++;
        }
        long trailXp = 0, featureXp = 0;
        for (int r = 0; r < Ratings; r++)
        {
            trailXp += lengthCm[r] * rules.XpPerKmByRating.For((TrailRating)r) / 100_000;
            featureXp += (long)features[r] * rules.XpPerFeatureByRating.For((TrailRating)r);
        }
        var visitors = state.Reputation.DailyVisitors;
        int average = visitors.Count == 0 ? 0 : (int)(visitors.Sum(v => (long)v) / visitors.Count);
        return new XpBreakdown(
            lengthCm.Select(cm => cm / 100).ToArray(), (int)trailXp,
            features, (int)featureXp,
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

    /// <summary>What reaching <paramref name="level"/> unlocks (features, lift types, land), for the level-up message.</summary>
    public static List<string> UnlocksAt(WorldState state, int level)
    {
        var unlocks = new List<string>();
        unlocks.AddRange(state.TrailFeatureTypes.Where(t => t.RequiredLevel == level).Select(t => $"{t.Name} ({t.Rating.ToString().ToLowerInvariant()})"));
        unlocks.AddRange(state.LiftTypes.Where(t => t.RequiredLevel == level).Select(t => t.Name));
        unlocks.AddRange(state.Parcels.Where(p => p.RequiredLevel == level && !state.OwnedParcelIds.Contains(p.Id)).Select(p => $"land: {p.Name}"));
        return unlocks;
    }

    /// <summary>XP needed for the next level, or null at the top.</summary>
    public static int? NextLevelXp(ReputationRules rules, int level) => level < rules.LevelXp.Count ? rules.LevelXp[level] : null;
}
