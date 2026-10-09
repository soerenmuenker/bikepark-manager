using System.Text.Json;
using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Reputation;
using Bikepark.Sim.Safety;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class SafetyTests
{
    // Starter Valley + demo_lift_network.json: Flow Country 11, Red Rocket 12.
    private const int FlowCountry = 11;
    private const int RedRocket = 12;

    private static CrashRules ValleyRules() => TestWorlds.LiftScenario().CrashRules;

    private static TrailFeatureType Feature(string id) => TestWorlds.FeatureCatalog().Single(t => t.Id == id);

    /// <summary>Starter Valley with the demo trails and these crash rules.</summary>
    private static Simulation Valley(CrashRules rules, IEnumerable<TimedCommand>? extra = null)
    {
        var scenario = TestWorlds.LiftScenario();
        scenario.CrashRules = rules;
        scenario.ReputationRules.Enabled = false; // full demand from day one: more riders, more statistics
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        foreach (var c in TestWorlds.DemoLiftNetwork().Concat(extra ?? []))
            sim.Commands.Enqueue(c.Command, c.Tick);
        return sim;
    }

    /// <summary>Everybody crashes at the end of their first 10 m of trail; all serious (or all minor).</summary>
    private static CrashRules CertainCrash(bool serious) => new()
    {
        Enabled = true,
        FeatureBasePpm = 0,
        CollisionPpm = 0,
        TerrainBasePpm = 1_000_000,
        MinSkillFactorPermille = 1000,
        OverSkillPer100Permille = 0,
        SeriousBasePermille = serious ? 1000 : 0,
        SeriousJumpPermille = 0,
        SeriousCollisionPermille = 0,
        SeriousPer100OverSkillPermille = 0,
    };

    // ---------------------------------------------------------------- rules and math

    [Fact]
    public void CrashesAreOffByDefault()
    {
        var sim = TestWorlds.Run(1337, 2, TestWorlds.DemoNetwork());
        Assert.False(sim.State.CrashRules.Enabled);
        Assert.Equal(0, sim.State.Safety.TotalCrashes);
        Assert.Equal(0, sim.State.Safety.TotalInsuranceCents);
        Assert.True(sim.State.Ways.Sum(w => w.Stats.Runs) > 100);
    }

    [Fact]
    public void Rules_AreValidated()
    {
        Assert.Empty(ValleyRules().Validate());
        Assert.NotEmpty(new CrashRules { HelicopterMinMinutes = 30, HelicopterMaxMinutes = 10 }.Validate());
        Assert.NotEmpty(new CrashRules { FeatureBasePpm = -1 }.Validate());
        Assert.NotEmpty(new CrashRules { InsuranceDays = 0 }.Validate());
    }

    [Fact]
    public void FeatureChance_RisesWithDifficultyOverSkill_Wear_Wet_AndFatigue()
    {
        var rules = ValleyRules();
        var wear = TestWorlds.LiftScenario().WearRules;
        var drop = Feature("drop");
        long Chance(int skill, int condition = 1000, int wet = 0, int energy = 1000) =>
            CrashMath.FeatureChancePpb(rules, wear, new Guest { Skill = skill, Energy = energy }, drop, condition, wet);

        long matched = Chance(drop.Difficulty);
        // The argument is the rider's skill: a better rider is safer, a weaker one much less so.
        Assert.True(Chance(drop.Difficulty + 200) > 0);
        Assert.True(Chance(drop.Difficulty + 200) < matched);
        Assert.True(Chance(drop.Difficulty - 200) > matched * 2);
        Assert.Equal(matched, Chance(drop.Difficulty, condition: wear.RoughBelowPermille)); // fine down to the rough level
        Assert.True(Chance(drop.Difficulty, condition: 100) > matched * 2);
        Assert.Equal(matched * 2, Chance(drop.Difficulty, wet: 1000));
        Assert.True(Chance(drop.Difficulty, energy: 100) > matched);

        var berm = Feature("berm");
        var rider = new Guest { Skill = 500, Energy = 1000 };
        var bermLike = new TrailFeatureType { Id = "x", Kind = FeatureKind.Berm, Difficulty = drop.Difficulty };
        Assert.True(CrashMath.FeatureChancePpb(rules, wear, rider, drop, 1000, 0) > CrashMath.FeatureChancePpb(rules, wear, rider, bermLike, 1000, 0));
        Assert.NotNull(berm);
    }

    [Fact]
    public void SeriousShare_RisesWithJumps_Collisions_AndOverSkill()
    {
        var rules = ValleyRules();
        int plain = CrashMath.SeriousPermille(rules, CrashCause.Terrain, jump: false, 500, 500);
        Assert.Equal(rules.SeriousBasePermille, plain);
        Assert.True(CrashMath.SeriousPermille(rules, CrashCause.Feature, jump: true, 500, 500) > plain);
        Assert.True(CrashMath.SeriousPermille(rules, CrashCause.Collision, jump: false, 500, 500) > plain);
        Assert.True(CrashMath.SeriousPermille(rules, CrashCause.Terrain, jump: false, 800, 500) > plain);
    }

    [Fact]
    public void Premium_IsABasePlusRecentAccidents()
    {
        var rules = ValleyRules();
        var safety = new SafetyState { History = [new(0, 2, 1), new(1, 0, 0), new(2, 1, 0)] };
        Assert.Equal(rules.InsuranceBaseCents + 3 * rules.InsurancePerMinorCents + rules.InsurancePerSeriousCents, CrashMath.Premium(rules, safety));
        Assert.Equal(rules.InsuranceBaseCents, CrashMath.Premium(rules, new SafetyState()));
    }

    // ---------------------------------------------------------------- crossings

    [Fact]
    public void Intersect_FindsCrossingSegments_NotParallelOnes()
    {
        Assert.True(Crossings.Intersect(0, 0, 1000, 0, 500, -500, 500, 500, out long t, out long u, out long den));
        Assert.Equal(500, 1000 * t / den);
        Assert.Equal(500, 1000 * u / den);
        Assert.False(Crossings.Intersect(0, 0, 1000, 0, 0, 100, 1000, 100, out _, out _, out _));
        Assert.False(Crossings.Intersect(0, 0, 1000, 0, 1500, -500, 1500, 500, out _, out _, out _));
    }

    [Fact]
    public void Network_FindsWhereTheDemoWaysCross_AtTheSameSpotOnBoth()
    {
        var sim = Valley(ValleyRules());
        sim.Step();
        var crossings = sim.Network.Crossings;
        Assert.Contains(crossings, c => c.WayA == FlowCountry && c.WayB == RedRocket);
        foreach (var c in crossings)
        {
            var a = sim.Network.Geometry(c.WayA).PositionAt(c.CmA);
            var b = sim.Network.Geometry(c.WayB).PositionAt(c.CmB);
            long dx = a.X - b.X, dz = a.Z - b.Z;
            Assert.True(dx * dx + dz * dz < 300 * 300, $"{c}: {dx}, {dz}");
            Assert.InRange(c.CmA, Crossings.EndMarginCm, sim.Network.Geometry(c.WayA).LengthCm - Crossings.EndMarginCm);
        }
        var onRed = sim.Network.CrossingsOn(RedRocket);
        Assert.Equal(crossings.Count(c => c.WayA == RedRocket || c.WayB == RedRocket), onRed.Count);
        Assert.True(onRed.Zip(onRed.Skip(1)).All(p => p.First.Cm <= p.Second.Cm));
    }

    [Fact]
    public void Planner_WarnsWhenANewWayCrossesAnother()
    {
        var demo = TestWorlds.DemoLiftNetwork();
        var sim = TestWorlds.RunLift(1337, 1, [demo[0]]); // Flow Country only
        var red = (BuildWayCommand)demo[1].Command;
        var plan = WayPlanner.Plan(sim.Terrain, sim.Network, sim.State.TrailRules, WayKind.Trail, red.Points);
        Assert.True(plan.IsValid);
        Assert.Contains(plan.Issues, i => i.Code == "crossing" && i.Severity == IssueSeverity.Warning && i.Message.Contains("Flow Country"));
    }

    // ---------------------------------------------------------------- crashes on the trail

    [Fact]
    public void SeriousCrash_BlocksTheTrail_UntilTheHelicopterHasFlownTheRiderOut()
    {
        var sim = Valley(CertainCrash(serious: true));
        var called = new List<HelicopterCalled>();
        var evacuated = new List<RiderEvacuated>();
        var left = new List<GuestLeft>();
        sim.Events.Subscribe<HelicopterCalled>(called.Add);
        sim.Events.Subscribe<RiderEvacuated>(evacuated.Add);
        sim.Events.Subscribe<GuestLeft>(left.Add);
        while (called.Count == 0 && sim.State.Tick < GameTime.MinutesPerDay)
        {
            sim.Step();
            sim.Events.Dispatch();
        }
        var call = Assert.Single(called);
        var victim = sim.State.Guests.Single(g => g.Id == call.GuestId);
        Assert.Equal(RiderActivity.Injured, victim.Activity);
        Assert.Equal(InjurySeverity.Serious, victim.Injury);
        Assert.InRange(call.RescueAtTick - call.Tick, sim.State.CrashRules.HelicopterMinMinutes, sim.State.CrashRules.HelicopterMaxMinutes);
        Assert.Equal(1, sim.Network.FindWay(call.WayId)!.Stats.SeriousCrashes);
        long progress = victim.RouteProgressCm;

        // Ten minutes later: still lying there, and nobody got past on that trail.
        sim.RunTicks(10);
        sim.Events.Dispatch();
        Assert.Equal(progress, victim.RouteProgressCm);
        Assert.True(RiderSystem.PositionOnWay(victim, out _, out long at));
        foreach (var other in sim.State.Guests.Where(g => g != victim && g.Activity == RiderActivity.Descending))
            if (RiderSystem.PositionOnWay(other, out int way, out long pos) && way == call.WayId)
                Assert.True(pos < at, $"rider {other.Id} at {pos} passed the crash at {at}");
        Assert.True(sim.Network.FindWay(call.WayId)!.Stats.HeldUpSeconds > 0);

        // The helicopter: the rider leaves, the trail is free.
        sim.RunTicks(call.RescueAtTick - sim.State.Tick + 1);
        sim.Events.Dispatch();
        Assert.Contains(evacuated, e => e.GuestId == victim.Id && e.Tick == call.RescueAtTick);
        Assert.Contains(left, l => l.GuestId == victim.Id && l.Reason == GuestLeaveReason.Evacuated);
        Assert.DoesNotContain(victim, sim.State.Guests);
        Assert.True(sim.State.Safety.Evacuations >= 1);
    }

    [Fact]
    public void MinorCrash_RidesDownSlowly_WithoutCrashingAgain_AndGoesHome()
    {
        var sim = Valley(CertainCrash(serious: false));
        var crashes = new List<RiderCrashed>();
        var left = new List<GuestLeft>();
        sim.Events.Subscribe<RiderCrashed>(crashes.Add);
        sim.Events.Subscribe<GuestLeft>(left.Add);
        while (crashes.Count == 0 && sim.State.Tick < GameTime.MinutesPerDay)
        {
            sim.Step();
            sim.Events.Dispatch();
        }
        var first = crashes[0];
        var rider = sim.State.Guests.Single(g => g.Id == first.GuestId);
        Assert.Equal(InjurySeverity.Minor, rider.Injury);
        Assert.Equal(RiderActivity.Descending, rider.Activity);

        long before = rider.RouteProgressCm;
        sim.Step();
        long slow = (long)sim.State.CrashRules.MinorSpeedCmPerS * 60;
        Assert.InRange(rider.RouteProgressCm - before, 0, slow);

        sim.RunTicks(GameTime.MinutesPerHour * 3);
        sim.Events.Dispatch();
        Assert.Single(crashes, c => c.GuestId == rider.Id); // no second crash on the way down
        Assert.Contains(left, l => l.GuestId == rider.Id && l.Reason == GuestLeaveReason.Injured);
    }

    // ---------------------------------------------------------------- done when: accident rate reacts to difficulty and condition

    /// <summary>Feature crashes per 1000 runs on each demo trail, with the demo features built and fixed at a condition.</summary>
    private static (double Flow, double Red, int Crashes) FeatureCrashRates(int conditionPermille)
    {
        var rules = ValleyRules();
        rules.FeatureBasePpm *= 20; // more crashes, steadier statistics
        rules.TerrainBasePpm = 0;
        rules.CollisionPpm = 0;
        var features = JsonSerializer.Deserialize<List<TimedCommand>>(
                File.ReadAllText(Path.Combine(TestWorlds.RepoRoot(), "data", "scripts", "demo_features.json")), SimJson.Indented)!
            .Select(c => c with { Command = ((PlaceTrailFeatureCommand)c.Command) with { Instant = true } });
        var sim = Valley(rules, features);
        sim.State.WearRules.WearPerPass = 0; // condition stays where the test puts it
        sim.Step();
        foreach (var feature in sim.State.Ways.SelectMany(w => w.Features))
            feature.Condition = conditionPermille * 1000;
        sim.RunDays(5);
        var flow = sim.State.Ways.Single(w => w.Id == FlowCountry).Stats;
        var red = sim.State.Ways.Single(w => w.Id == RedRocket).Stats;
        return (flow.Crashes * 1000.0 / Math.Max(1, flow.Runs), red.Crashes * 1000.0 / Math.Max(1, red.Runs), flow.Crashes + red.Crashes);
    }

    [Fact]
    public void AccidentRate_ReactsToDifficulty_AndCondition()
    {
        var fresh = FeatureCrashRates(1000);
        var worn = FeatureCrashRates(100);
        Assert.True(fresh.Crashes > 20, $"{fresh.Crashes} crashes");
        Assert.True(fresh.Red > fresh.Flow * 1.5, $"red {fresh.Red:0.0} vs flow {fresh.Flow:0.0} per 1000 runs");
        Assert.True(worn.Red > fresh.Red * 1.5, $"worn {worn.Red:0.0} vs fresh {fresh.Red:0.0} per 1000 runs on Red Rocket");
        Assert.True(worn.Flow > fresh.Flow, $"worn {worn.Flow:0.0} vs fresh {fresh.Flow:0.0} per 1000 runs on Flow Country");
    }

    // ---------------------------------------------------------------- consequences

    [Fact]
    public void Crashes_MakeReviewsFeelUnsafe()
    {
        var rules = TestWorlds.LiftScenario().ReputationRules;
        Guest Visit(InjurySeverity injury, int seen = 0) => new() { Skill = 500, RunsCompleted = 4, VisitFunSum = 2400, Injury = injury, CrashesSeen = seen };
        Assert.Equal(1000, ReviewMath.Score(rules, Visit(InjurySeverity.None), 180).Safety);
        Assert.Equal(0, ReviewMath.Score(rules, Visit(InjurySeverity.Serious), 180).Safety);
        Assert.InRange(ReviewMath.Score(rules, Visit(InjurySeverity.Minor), 180).Safety, 0, 200);
        Assert.Equal(700, ReviewMath.Score(rules, Visit(InjurySeverity.None, seen: 2), 180).Safety);
    }

    [Fact]
    public void Insurance_IsChargedDaily_FromTheLastDaysAccidents()
    {
        var sim = Valley(ValleyRules());
        var days = new List<DayReport>();
        sim.Events.Subscribe<DayEnded>(e => days.Add(e.Report));
        sim.RunDays(9);
        sim.Events.Dispatch();
        var rules = sim.State.CrashRules;
        Assert.Equal(rules.InsuranceDays, sim.State.Safety.History.Count);
        Assert.Equal(days.Sum(d => d.InsuranceCents), sim.State.Safety.TotalInsuranceCents);
        Assert.All(days, d => Assert.True(d.InsuranceCents >= rules.InsuranceBaseCents));
        Assert.Equal(sim.State.Safety.TotalCrashes, days.Sum(d => (long)d.Crashes) + sim.State.Safety.MinorToday + sim.State.Safety.SeriousToday);
        Assert.True(sim.State.Safety.TotalCrashes > 0);
    }

    [Fact]
    public void MidRescue_SaveAndResume_IsDeterministic()
    {
        var sim = Valley(CertainCrash(serious: true));
        while (!sim.State.Guests.Any(g => g.Activity == RiderActivity.Injured))
            sim.Step();
        var loaded = new Simulation(SaveGame.Clone(sim.State));
        sim.RunTicks(120);
        loaded.RunTicks(120);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded.State));
    }
}
