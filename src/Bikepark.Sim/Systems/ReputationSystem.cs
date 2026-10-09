using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Systems;

/// <summary>
/// Books influencer visits at opening (the next guest admitted is the influencer), and at the end of each day records
/// the day's visitors and announces level changes. Reviews are written by <see cref="GuestSystem"/> through
/// <see cref="Review"/> when a guest leaves. Without <see cref="ReputationRules.Enabled"/> no reviews are written and no
/// influencers come; the visitor history and the level are kept either way (XP is derived).
/// </summary>
internal sealed class ReputationSystem : ISimSystem
{
    private const int MaxPosts = 10;

    public void Update(SimContext ctx)
    {
        var state = ctx.State;
        var rules = state.ReputationRules;
        var rep = state.Reputation;
        long day = GameTime.Day(ctx.Tick);

        if (rules.Enabled && rules.InfluencerChancePermille > 0 && GameTime.MinuteOfDay(ctx.Tick) == state.Rules.OpenMinute
            && day >= rules.InfluencerFirstDay && (rep.LastInfluencerDay < 0 || day - rep.LastInfluencerDay >= rules.InfluencerMinGapDays)
            && ctx.Rng.ChancePermille(rules.InfluencerChancePermille))
        {
            rep.InfluencerDue = true;
            rep.LastInfluencerDay = day;
        }

        if (!GameTime.IsLastMinuteOfDay(ctx.Tick))
            return;

        rep.InfluencerDue = false; // nobody came today
        rep.DailyVisitors.Add(state.Stats.VisitorsToday);
        if (rep.DailyVisitors.Count > rules.VisitorDays)
            rep.DailyVisitors.RemoveRange(0, rep.DailyVisitors.Count - rules.VisitorDays);

        int level = ParkProgress.Level(rules, ParkProgress.Xp(state, ctx.Network).Total);
        if (level != rep.Level)
        {
            ctx.Publish(new LevelChanged(ctx.Tick, rep.Level, level));
            rep.Level = level;
        }
    }

    /// <summary>
    /// A leaving guest may write a review (only after at least one run, with <see cref="ReputationRules.ReviewChancePermille"/>);
    /// an influencer always does, and it becomes their post.
    /// </summary>
    internal static void Review(SimContext ctx, Guest guest)
    {
        var state = ctx.State;
        var rules = state.ReputationRules;
        if (!rules.Enabled) return;
        if (!guest.IsInfluencer && (guest.RunsCompleted == 0 || !ctx.Rng.ChancePermille(rules.ReviewChancePermille)))
            return;

        var review = ReviewMath.Write(rules, guest, ctx.Tick);
        ReputationMath.Add(state, review);
        ctx.Publish(new ReviewWritten(ctx.Tick, guest.Id, review.Group, review.StarsTenths, guest.IsInfluencer));
        if (!guest.IsInfluencer) return;

        var post = new InfluencerPost(review.Day, guest.Id, ReputationMath.InfluencerName(guest.Id), review.StarsTenths,
            ReputationMath.PostEffect(rules, review.StarsTenths), review.Day + rules.InfluencerEffectDays);
        var posts = state.Reputation.Posts;
        posts.Add(post);
        if (posts.Count > MaxPosts)
            posts.RemoveRange(0, posts.Count - MaxPosts);
        ctx.Publish(new InfluencerPosted(ctx.Tick, post));
    }
}
