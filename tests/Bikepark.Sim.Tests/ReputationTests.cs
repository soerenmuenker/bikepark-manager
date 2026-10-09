using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;

namespace Bikepark.Sim.Tests;

public class ReputationTests
{
    // Starter Valley + demo_lift_network.json: Flow Country 11, Red Rocket 12.
    private const int RedRocket = 12;

    private static ReputationRules ValleyRules() => TestWorlds.LiftScenario().ReputationRules;

    private static Review Review(SkillGroup group, int stars, long day = 0) => new(day, 1, group, stars, 0, 0, 0, 0, 0, 0);

    // ---------------------------------------------------------------- rules

    [Fact]
    public void ReputationIsOffByDefault_FullDemand_NoReviews()
    {
        var sim = TestWorlds.Run(1337, 1, TestWorlds.DemoNetwork());
        Assert.False(sim.State.ReputationRules.Enabled);
        Assert.Equal(1000, ReputationMath.DemandPermille(sim.State));
        Assert.Equal(0, sim.State.Reputation.TotalReviews);
        Assert.True(sim.State.Stats.TotalGuestsLeft > 0);
    }

    [Fact]
    public void Rules_AreValidated()
    {
        Assert.Empty(ValleyRules().Validate());
        var rules = ValleyRules();
        rules.Groups[0].Fun += 1; // weights no longer sum to 1000
        Assert.Contains(rules.Validate(), e => e.Contains("sum to 1000"));
        Assert.NotEmpty(new ReputationRules { Enabled = true }.Validate()); // no groups
        Assert.NotEmpty(new ReputationRules { LevelXp = [100, 50] }.Validate());
        Assert.NotEmpty(new ReputationRules { VisibilityCurve = [new(10, 500), new(5, 600)] }.Validate());
    }

    // ---------------------------------------------------------------- new park

    [Fact]
    public void NewPark_HasNoRating_IsLevelZero_AndStartsAtTheVisibilityFloor()
    {
        var sim = TestWorlds.RunLift(1337, 0);
        Assert.Null(ReputationMath.RatingTenths(sim.State));
        Assert.Equal(0, ParkProgress.Level(sim.State.ReputationRules, ParkProgress.Xp(sim.State, sim.Network).Total));
        var demand = ReputationMath.Demand(sim.State);
        Assert.Equal(500, demand.Visibility);
        Assert.Equal(1000, demand.Rating);
        Assert.Equal(500, demand.Total);
    }

    [Fact]
    public void GuestsWriteReviews_TheRatingAppears_AndDemandGrows()
    {
        var sim = TestWorlds.RunLift(1337, 0, TestWorlds.DemoLiftNetwork());
        var reviews = new List<ReviewWritten>();
        sim.Events.Subscribe<ReviewWritten>(reviews.Add);
        sim.RunDays(3);
        sim.Events.Dispatch();

        var rep = sim.State.Reputation;
        Assert.True(rep.TotalReviews > 100, $"{rep.TotalReviews} reviews");
        Assert.Equal(rep.TotalReviews, reviews.Count);
        Assert.Equal(rep.TotalReviews, rep.BeginnerReviews + rep.IntermediateReviews + rep.ExpertReviews);
        Assert.Equal(rep.TotalReviews, rep.Reviews.Count); // all still in the window
        Assert.All(rep.Reviews, r => Assert.InRange(r.StarsTenths, 10, 50));
        Assert.True(rep.TotalReviews < sim.State.Stats.TotalGuestsLeft, "not everybody writes a review");
        Assert.NotNull(ReputationMath.RatingTenths(sim.State));
        Assert.True(ReputationMath.Demand(sim.State).Visibility > 500);
    }

    // ---------------------------------------------------------------- reviews

    [Fact]
    public void TheSameVisit_IsRatedByWhatEachGroupValues()
    {
        var rules = ValleyRules();
        // Five runs down a black line with good jumps, little waiting.
        Guest Visit(int skill) => new()
        {
            Id = 1, Skill = skill, Happiness = 700, ArrivedTick = 0, RunsCompleted = 5, VisitFunSum = 5 * 650,
            HardestDifficulty = 750, ScaredRuns = skill + rules.ScaredMargin < 750 ? 5 : 0,
            JumpCount = 10, JumpFunSum = 10 * 700, TrailsRidden = [1], QueueMinutes = 5,
        };
        var beginner = ReviewMath.Write(rules, Visit(200), 240);
        var expert = ReviewMath.Write(rules, Visit(800), 240);

        Assert.Equal(SkillGroup.Beginner, beginner.Group);
        Assert.Equal(SkillGroup.Expert, expert.Group);
        Assert.Equal(0, beginner.Safety);
        Assert.Equal(1000, expert.Safety);
        Assert.Equal(1000, expert.Challenge);
        Assert.True(expert.StarsTenths >= beginner.StarsTenths + 10, $"expert {expert.StarsTenths}, beginner {beginner.StarsTenths}");
    }

    [Fact]
    public void Waiting_AndBeingHeldUp_CostSmoothness()
    {
        var rules = ValleyRules();
        var calm = new Guest { Skill = 500, RunsCompleted = 3, VisitFunSum = 1500 };
        var queued = new Guest { Skill = 500, RunsCompleted = 3, VisitFunSum = 1500, QueueMinutes = 40, HeldUpSeconds = 600 };
        Assert.Equal(1000, ReviewMath.Score(rules, calm, 180).Smoothness);
        Assert.True(ReviewMath.Score(rules, queued, 180).Smoothness < 500);
        Assert.Equal(SkillGroup.Beginner, ReviewMath.GroupOf(rules, 349));
        Assert.Equal(SkillGroup.Intermediate, ReviewMath.GroupOf(rules, 350));
        Assert.Equal(SkillGroup.Expert, ReviewMath.GroupOf(rules, 650));
    }

    [Fact]
    public void Value_IsTheRidingGotForTheFeePaid()
    {
        var rules = ValleyRules();
        rules.FairCentsPerRun = 250; // a run is worth 2.50 €
        Guest Visit(long paid, int runs) => new() { Skill = 500, RunsCompleted = runs, VisitFunSum = runs * 500L, PaidEntryCents = paid };
        Assert.Equal(1000, ReviewMath.Score(rules, Visit(1500, 6), 180).Value);
        Assert.Equal(500, ReviewMath.Score(rules, Visit(1500, 3), 180).Value);
        Assert.Equal(500, ReviewMath.Score(rules, Visit(3000, 6), 180).Value);
        Assert.Equal(1000, ReviewMath.Score(rules, Visit(0, 1), 180).Value); // a free day is always worth it

        // Same visit, pricier ticket: fewer stars, most for the groups that care about value.
        int Stars(SkillGroup group, long paid) => ReviewMath.Stars(rules, group, ReviewMath.Score(rules, Visit(paid, 4), 180));
        Assert.True(Stars(SkillGroup.Intermediate, 1000) > Stars(SkillGroup.Intermediate, 4000));
        Assert.True(Stars(SkillGroup.Intermediate, 1000) - Stars(SkillGroup.Intermediate, 4000)
                    > Stars(SkillGroup.Expert, 1000) - Stars(SkillGroup.Expert, 4000));
    }

    [Fact]
    public void Guests_RememberTheFeeTheyPaid()
    {
        var sim = TestWorlds.RunLift(1337, 9 * 60 + 30);
        Assert.NotEmpty(sim.State.Guests);
        Assert.All(sim.State.Guests, g => Assert.Equal(sim.State.Park.EntryFeeCents, g.PaidEntryCents));
    }

    [Fact]
    public void Rating_IsHiddenBelowTheMinimum_AndFollowsTheRecentWindow()
    {
        var state = TestWorlds.Create();
        state.ReputationRules = ValleyRules();
        state.ReputationRules.WindowSize = 20;

        for (int i = 0; i < 9; i++) ReputationMath.Add(state, Review(SkillGroup.Expert, 10));
        Assert.Null(ReputationMath.RatingTenths(state));
        ReputationMath.Add(state, Review(SkillGroup.Expert, 10));
        Assert.Equal(10, ReputationMath.RatingTenths(state));
        Assert.Equal(10, ReputationMath.RatingTenths(state, SkillGroup.Expert));
        Assert.Null(ReputationMath.RatingTenths(state, SkillGroup.Beginner));

        // A good stretch pushes the bad reviews out of the window: the park recovers.
        for (int i = 0; i < 20; i++) ReputationMath.Add(state, Review(SkillGroup.Expert, 50));
        Assert.Equal(20, state.Reputation.Reviews.Count);
        Assert.Equal(30, state.Reputation.TotalReviews);
        Assert.Equal(50, ReputationMath.RatingTenths(state));

        // Windows are per group: beginner reviews don't push expert ones out.
        for (int i = 0; i < 10; i++) ReputationMath.Add(state, Review(SkillGroup.Beginner, 20));
        Assert.Equal(30, state.Reputation.Reviews.Count);
        Assert.Equal(40, ReputationMath.RatingTenths(state)); // (20 x 50 + 10 x 20) / 30
        Assert.Equal(20, ReputationMath.RatingTenths(state, SkillGroup.Beginner));
    }

    [Fact]
    public void DemandFollowsReviewVolume_AndRating_WithinTheClamp()
    {
        var state = TestWorlds.Create();
        state.ReputationRules = ValleyRules();
        for (int i = 0; i < 1500; i++) ReputationMath.Add(state, Review(SkillGroup.Intermediate, 50));
        var good = ReputationMath.Demand(state);
        Assert.Equal(1000, good.Visibility);
        Assert.Equal(1300, good.Rating);
        Assert.Equal(1300, good.Total);

        for (int i = 0; i < 200; i++) ReputationMath.Add(state, Review(SkillGroup.Intermediate, 10));
        var bad = ReputationMath.Demand(state);
        Assert.Equal(700, bad.Rating);
        Assert.True(bad.Total < good.Total);
    }

    // ---------------------------------------------------------------- influencers

    [Fact]
    public void PostEffect_ScalesWithStars_AndFades()
    {
        var rules = ValleyRules();
        Assert.Equal(400, ReputationMath.PostEffect(rules, 50));
        Assert.Equal(0, ReputationMath.PostEffect(rules, 30));
        Assert.Equal(-400, ReputationMath.PostEffect(rules, 10));
        Assert.Equal(200, ReputationMath.PostEffect(rules, 40));

        var state = TestWorlds.Create();
        state.ReputationRules = rules;
        state.Reputation.Posts.Add(new InfluencerPost(4, 1, "@Test", 50, 400, 9));
        Assert.Equal(0, ReputationMath.InfluencerEffect(state, 3));
        Assert.Equal(400, ReputationMath.InfluencerEffect(state, 4));
        Assert.Equal(160, ReputationMath.InfluencerEffect(state, 7));
        Assert.Equal(0, ReputationMath.InfluencerEffect(state, 9));
    }

    [Fact]
    public void AnInfluencerVisits_RidesTheDay_AndPosts()
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.ReputationRules.InfluencerChancePermille = 1000;
        scenario.ReputationRules.InfluencerFirstDay = 1;
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        foreach (var c in TestWorlds.DemoLiftNetwork()) sim.Commands.Enqueue(c.Command, c.Tick);
        var arrived = new List<InfluencerArrived>();
        var posted = new List<InfluencerPosted>();
        sim.Events.Subscribe<InfluencerArrived>(arrived.Add);
        sim.Events.Subscribe<InfluencerPosted>(posted.Add);

        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.Empty(arrived); // not before influencerFirstDay

        sim.RunTicks(GameTime.MinutesPerHour * 12);
        sim.Events.Dispatch();
        var influencer = Assert.Single(arrived);
        Assert.Equal(1, GameTime.Day(influencer.Tick));
        // Booked at opening: the first guest admitted after that is the influencer.
        Assert.InRange(GameTime.MinuteOfDay(influencer.Tick), sim.State.Rules.OpenMinute + 1, sim.State.Rules.OpenMinute + 30);
        if (sim.State.Guests.FirstOrDefault(g => g.Id == influencer.GuestId) is { } guest)
        {
            Assert.True(guest.IsInfluencer);
            Assert.Equal(scenario.ReputationRules.InfluencerSkill, guest.Skill);
        }

        sim.RunTicks(GameTime.MinutesPerHour * 12);
        sim.Events.Dispatch();
        var post = Assert.Single(posted).Post;
        Assert.Equal(influencer.GuestId, post.GuestId);
        Assert.Equal(post, Assert.Single(sim.State.Reputation.Posts));
        Assert.Equal(post.Day + scenario.ReputationRules.InfluencerEffectDays, post.EndsDay);
        Assert.Equal(ReputationMath.PostEffect(scenario.ReputationRules, post.StarsTenths), post.EffectPermille);
        Assert.Contains(sim.State.Reputation.Reviews, r => r.Influencer && r.GuestId == post.GuestId);

        // The minimum gap: nobody the next days.
        sim.RunDays(3);
        sim.Events.Dispatch();
        Assert.Single(arrived);
    }

    // ---------------------------------------------------------------- XP and level

    [Fact]
    public void Level_FollowsTheThresholds()
    {
        var rules = ValleyRules();
        Assert.Equal(0, ParkProgress.Level(rules, 0));
        Assert.Equal(0, ParkProgress.Level(rules, rules.LevelXp[0] - 1));
        Assert.Equal(1, ParkProgress.Level(rules, rules.LevelXp[0]));
        Assert.Equal(ReputationRules.MaxLevel, ParkProgress.Level(rules, rules.LevelXp[^1]));
        Assert.Equal(ReputationRules.MaxLevel, ParkProgress.Level(rules, int.MaxValue));
        Assert.Equal(rules.LevelXp[1], ParkProgress.NextLevelXp(rules, 1));
        Assert.Null(ParkProgress.NextLevelXp(rules, ReputationRules.MaxLevel));
        Assert.Equal(0, ParkProgress.Level(new ReputationRules(), 10_000)); // no thresholds
    }

    [Fact]
    public void Xp_ComesFromTrails_Diversity_AndVisitors_AndFallsWhenATrailGoes()
    {
        var sim = TestWorlds.RunLift(1337, 1, TestWorlds.DemoLiftNetwork());
        var trails = ParkProgress.Xp(sim.State, sim.Network);
        Assert.True(trails.TrailMeters > 1000, $"{trails.TrailMeters} m");
        Assert.Equal(2, trails.Ratings);
        Assert.Equal(0, trails.FeatureKinds);
        Assert.Equal(0, trails.AverageVisitors);

        var levels = new List<LevelChanged>();
        sim.Events.Subscribe<LevelChanged>(levels.Add);
        sim.RunDays(2);
        sim.Events.Dispatch();
        var busy = ParkProgress.Xp(sim.State, sim.Network);
        Assert.Equal(2, sim.State.Reputation.DailyVisitors.Count);
        Assert.True(busy.AverageVisitors > 50);
        Assert.Equal(trails.TrailXp + trails.RatingXp + busy.VisitorXp, busy.Total);
        Assert.Equal(ParkProgress.Level(sim.State.ReputationRules, busy.Total), sim.State.Reputation.Level);
        Assert.NotEmpty(levels);
        Assert.Equal(sim.State.Reputation.Level, levels[^1].NewLevel);

        sim.Commands.Enqueue(new DeleteWayCommand(RedRocket));
        sim.Step();
        var fewer = ParkProgress.Xp(sim.State, sim.Network);
        Assert.True(fewer.TrailMeters < busy.TrailMeters);
        Assert.Equal(1, fewer.Ratings);
        Assert.True(fewer.Total < busy.Total);
    }

    // ---------------------------------------------------------------- saves and determinism

    [Fact]
    public void ReputationState_SurvivesASave_AndRunsDeterministically()
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.ReputationRules.InfluencerChancePermille = 1000;
        scenario.ReputationRules.InfluencerFirstDay = 1;

        Simulation Run()
        {
            var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
            foreach (var c in TestWorlds.DemoLiftNetwork()) sim.Commands.Enqueue(c.Command, c.Tick);
            sim.RunDays(3);
            return sim;
        }

        var a = Run();
        var b = Run();
        Assert.Equal(StateHash.Compute(a.State), StateHash.Compute(b.State));
        Assert.NotEmpty(a.State.Reputation.Posts);

        string json = SaveGame.Serialize(a.State);
        var loaded = SaveGame.Deserialize(json);
        Assert.Equal(json, SaveGame.Serialize(loaded));
        Assert.Equal(a.State.Reputation.TotalReviews, loaded.Reputation.TotalReviews);
        Assert.Equal(a.State.Reputation.Posts, loaded.Reputation.Posts);

        // Continuing from the save gives the same park as continuing the original.
        var resumed = new Simulation(loaded);
        resumed.RunDays(1);
        a.RunDays(1);
        Assert.Equal(StateHash.Compute(a.State), StateHash.Compute(resumed.State));
    }
}
