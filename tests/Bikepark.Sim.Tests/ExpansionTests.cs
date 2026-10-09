using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Safety;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;

namespace Bikepark.Sim.Tests;

public class ExpansionTests
{
    // Career ids (see TestWorlds.CareerScenario): Old Ski Lift 1, Ski Hill Parking 6, Ski Hill Track 8, Valley Gondola 9.
    private const int SkiLift = 1;
    private const int SkiHillParking = 6;
    private const int SkiHillTrack = 8;
    private const int Gondola = 9;

    private static PointCm M(int x, int z) => new(x * 100, z * 100);

    private static Simulation Career(IEnumerable<TimedCommand>? commands = null, Action<ScenarioDefinition>? tweak = null)
    {
        var scenario = TestWorlds.CareerScenario();
        tweak?.Invoke(scenario);
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        foreach (var c in commands ?? []) sim.Commands.Enqueue(c.Command, c.Tick);
        return sim;
    }

    private static string? Reject(Simulation sim, ICommand command)
    {
        sim.Events.Clear();
        sim.Commands.Enqueue(command);
        sim.Step();
        return sim.Events.Pending.OfType<CommandRejected>().SingleOrDefault()?.Reason;
    }

    private static Lift Lift(Simulation sim, int id) => sim.State.Lifts.Single(l => l.Id == id);

    // ---------------------------------------------------------------- the career start

    [Fact]
    public void Career_LoadsWithADerelictTBar_AndALockedGondola()
    {
        var sim = Career();
        sim.Step();
        Assert.Empty(sim.Events.Pending.OfType<CommandRejected>());
        var tbar = Lift(sim, SkiLift);
        Assert.True(tbar.Derelict);
        Assert.False(tbar.InService);
        Assert.Equal(0, tbar.BikeCarrierPermille);
        Assert.Equal(0, Lift(sim, Gondola).BikeCarrierPermille);
        Assert.Equal(SkiHillParking, sim.Network.BaseHub?.Id); // guests arrive at the ski hill
        Assert.Equal(15_000_000, sim.State.Finance.MoneyCents);
        Assert.Empty(sim.State.Crew);
    }

    [Fact]
    public void Career_GuestsPedalUpTheTrack_UntilTheTBarIsRestored_AndNeverUseTheGondola()
    {
        var sim = Career(TestWorlds.CareerOpening());
        // Days 1 and 2: the T-bar is being restored, riders pedal up the track.
        int pedalling = 0;
        for (int minute = 0; minute < 2 * GameTime.MinutesPerDay; minute += 10)
        {
            sim.RunTicks(10);
            pedalling += sim.State.Guests.Count(g => g.Activity == RiderActivity.Climbing && g.Route.Count > g.LegIndex
                                                      && g.Route[g.LegIndex].WayId == SkiHillTrack);
        }
        Assert.Equal(0, Lift(sim, SkiLift).Stats.Riders);
        Assert.True(pedalling > 10, $"{pedalling} rider-samples on the track");

        sim.RunDays(2);
        Assert.True(Lift(sim, SkiLift).InService);
        Assert.True(Lift(sim, SkiLift).Stats.Riders > 100);
        Assert.Equal(0, Lift(sim, Gondola).Stats.Riders);
        Assert.Contains("foot_forest", sim.State.OwnedParcelIds);
    }

    [Fact]
    public void Career_IsDeterministic()
    {
        var a = Career(TestWorlds.CareerOpening());
        var b = Career(TestWorlds.CareerOpening());
        a.RunDays(4);
        b.RunDays(4);
        Assert.Equal(StateHash.Compute(a.State), StateHash.Compute(b.State));
    }

    // ---------------------------------------------------------------- lifts the park owns

    [Fact]
    public void RestoringTheTBar_CostsHalf_AndItRunsFromTheNextOpeningAfterTheWork()
    {
        var sim = Career();
        sim.Step();
        var type = LiftNetwork.FindType(sim.State, "tbar")!;
        long money = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new RestoreLiftCommand(SkiLift)));
        Assert.Equal(money - type.BuildCostCents / 2, sim.State.Finance.MoneyCents);
        var tbar = Lift(sim, SkiLift);
        long ready = GameTime.TicksForDays(type.RestoreDays) + sim.State.Rules.OpenMinute - sim.State.Rules.LiftWarmupMinutes;
        Assert.Equal(ready, tbar.ReadyTick);
        Assert.Contains("already being restored", Reject(sim, new RestoreLiftCommand(SkiLift)));

        sim.RunTicks(ready - sim.State.Tick);
        Assert.False(tbar.InService);
        sim.Events.Clear();
        sim.Step();
        Assert.True(tbar.InService);
        Assert.Equal(1000, tbar.BikeCarrierPermille);
        Assert.Contains(sim.Events.Pending, e => e is LiftReady r && r.LiftId == SkiLift);
        Assert.Contains("not derelict", Reject(sim, new RestoreLiftCommand(SkiLift)));
    }

    [Fact]
    public void BuildingALift_NeedsItsLevel_ThenCostsMoney_TakesDays_AndUpkeep()
    {
        var sim = Career();
        sim.Step();
        // A chairlift on the ski hill: level 5 needed.
        Assert.Contains("needs park level 5", Reject(sim, new BuildLiftCommand("chairlift_4", "Chair", M(760, 890), M(745, 700))));

        // A second T-bar on the hill (level 0): paid now, ready after its build days, upkeep from then on.
        var type = LiftNetwork.FindType(sim.State, "tbar")!;
        long money = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new BuildLiftCommand("tbar", "New T-bar", M(700, 890), M(705, 730))));
        var lift = sim.State.Lifts[^1];
        Assert.Equal(money - type.BuildCostCents, sim.State.Finance.MoneyCents);
        Assert.False(lift.InService);
        Assert.Equal(GameTime.TicksForDays(type.BuildDays) + sim.State.Rules.OpenMinute - sim.State.Rules.LiftWarmupMinutes, lift.ReadyTick);

        var days = new List<DayReport>();
        sim.Events.Subscribe<DayEnded>(e => days.Add(e.Report));
        sim.RunDays(type.BuildDays + 1);
        sim.Events.Dispatch();
        Assert.True(lift.InService);
        Assert.Equal(0, days[0].LiftUpkeepCents);
        Assert.Equal(type.UpkeepPerDayCents, days[^1].LiftUpkeepCents);
        Assert.Equal(sim.State.Finance.TotalLiftUpkeepCents, days.Sum(d => d.LiftUpkeepCents));
    }

    [Fact]
    public void DebugInstantLifts_AreFree_AndRunAtOnce()
    {
        var sim = TestWorlds.RunLift(1337, 1);
        long money = sim.State.Finance.MoneyCents;
        Assert.Null(Reject(sim, new BuildLiftCommand("tbar", "Debug", M(800, 900), M(790, 720), Instant: true)));
        Assert.Equal(money, sim.State.Finance.MoneyCents);
        Assert.True(sim.State.Lifts[^1].InService);
    }

    // ---------------------------------------------------------------- the company gondola

    [Fact]
    public void GondolaBikeAccess_NeedsTheLandAtBothStations()
    {
        var sim = Career();
        sim.Step();
        Assert.Contains("Buy the land at both stations", Reject(sim, new SetLiftBikeAccessCommand(Gondola, 1)));
        sim.State.OwnedParcelIds.Add("valley_meadows");
        Assert.Contains("Buy the land at both stations", Reject(sim, new SetLiftBikeAccessCommand(Gondola, 1)));
        sim.State.OwnedParcelIds.Add("plateau");
        Assert.Null(Reject(sim, new SetLiftBikeAccessCommand(Gondola, 1)));
        Assert.Null(Reject(sim, new SetLiftBikeAccessCommand(Gondola, 0))); // "no bikes" is always allowed
    }

    // ---------------------------------------------------------------- rain on open lifts

    [Fact]
    public void TBarsAndChairs_LeaveRidersInTheRain_GondolasDont()
    {
        var sim = Career();
        sim.Step();
        Guest On(int liftId) => new() { Activity = RiderActivity.OnLift, Route = [new RouteLeg(liftId, 0, 1000, LegKind.Lift)] };
        Assert.False(GuestSystem.OnShelteredLift(sim.State, On(SkiLift)));
        Assert.True(GuestSystem.OnShelteredLift(sim.State, On(Gondola)));
    }

    // ---------------------------------------------------------------- the T-bar track

    /// <summary>The career opening with its trails built at once (Old Piste zigzags across the T-bar track).</summary>
    private static IEnumerable<TimedCommand> OpeningBuiltAtOnce() =>
        TestWorlds.CareerOpening().Select(c => c.Command is BuildWayCommand build ? c with { Command = build with { Instant = true } } : c);

    private static BuildWayCommand OldPiste() =>
        (BuildWayCommand)TestWorlds.CareerOpening().Select(c => c.Command).OfType<BuildWayCommand>().First(b => b.Name == "Old Piste");

    [Fact]
    public void TrailsCrossingATBarTrack_AreWarnedAbout_AndFoundInTheNetwork()
    {
        var sim = Career();
        sim.Step();
        var plan = WayPlanner.Plan(sim.Terrain, sim.Network, sim.State.TrailRules, WayKind.Trail, OldPiste().Points,
            Bikepark.Sim.Land.LandMath.OwnedPredicate(sim.State));
        Assert.True(plan.IsValid, plan.FirstError);
        Assert.Contains(plan.Issues, i => i.Code == "crossing" && i.Message.Contains("Old Ski Lift track"));

        Assert.Null(Reject(sim, OldPiste() with { Instant = true }));
        var piste = sim.State.Ways.Single(w => w.Name == "Old Piste");
        var crossings = sim.Network.TowCrossingsOn(piste.Id);
        Assert.True(crossings.Count >= 2, $"{crossings.Count} crossings");
        Assert.All(crossings, c => Assert.Equal(SkiLift, c.LiftId));
        long liftLength = sim.Network.Links.Single(l => l.Id == SkiLift).LengthCm;
        Assert.All(crossings, c => Assert.InRange(c.LiftCm, 1, liftLength - 1));

        // ... and a new T-bar whose track would cross the trail is warned about too.
        var tbar = StructurePlanner.PlanLift(sim.Terrain, sim.Network, sim.State, "tbar", M(705, 885), M(715, 715));
        Assert.Contains(tbar.Issues, i => i.Code == "crossing" && i.Message.Contains("Old Piste"));
    }

    [Fact]
    public void RidersBoardedInTheSameMinute_AreSpreadAlongTheLine()
    {
        var sim = Career(OpeningBuiltAtOnce());
        sim.RunTicks(3 * GameTime.MinutesPerDay + 11 * 60);
        var type = LiftNetwork.FindType(sim.State, "tbar")!;
        // Where each rider is along the lift leg (progress counts the whole route, e.g. the walk from the parking lot).
        var onLift = sim.State.Guests.Where(g => g.Activity == RiderActivity.OnLift)
            .Select(g => g.RouteProgressCm - g.Route.Take(g.LegIndex).Sum(l => l.LengthCm)).Order().ToList();
        Assert.True(onLift.Count >= 5, $"{onLift.Count} on the T-bar");
        long spacing = (long)type.IntervalSeconds * type.SpeedCmPerS;
        Assert.All(onLift.Zip(onLift.Skip(1)), p => Assert.True(p.Second - p.First >= spacing - 100, $"{p.First} → {p.Second}"));
    }

    [Fact]
    public void TrailRiders_CanCollideWithRidersOnTheTBar()
    {
        var sim = Career(OpeningBuiltAtOnce(), s =>
        {
            s.CrashRules.FeatureBasePpm = 0;
            s.CrashRules.TerrainBasePpm = 0;
            s.CrashRules.CollisionPpm = 1_000_000;
            s.CrashRules.CrossingWindowCm = 3_000;
        });
        var crashes = new List<RiderCrashed>();
        sim.Events.Subscribe<RiderCrashed>(crashes.Add);
        var towedVictims = new List<int>();
        for (int i = 0; i < 4 * GameTime.MinutesPerDay && towedVictims.Count == 0; i++)
        {
            var onLift = sim.State.Guests.Where(g => g.Activity == RiderActivity.OnLift).Select(g => g.Id).ToHashSet();
            sim.Step();
            sim.Events.Dispatch();
            towedVictims.AddRange(crashes.Where(c => c.Tick == sim.State.Tick - 1 && onLift.Contains(c.GuestId)).Select(c => c.GuestId));
        }
        var victim = Assert.Single(towedVictims.Take(1));
        var crash = crashes.First(c => c.GuestId == victim);
        Assert.Equal(CrashCause.Collision, crash.Cause);
        Assert.Contains(crashes, c => c.Tick == crash.Tick && c.GuestId != victim && c.WayId == crash.WayId); // the trail rider too

        // The T-bar stops until the crash is resolved: nobody boards, nobody on it moves.
        var tbar = Lift(sim, SkiLift);
        var rules = sim.State.CrashRules;
        long expected = crash.Severity == InjurySeverity.Serious
            ? sim.State.Guests.Single(g => g.Id == victim).RescueAtTick
            : crash.Tick + rules.TowStopMinutes;
        Assert.True(tbar.StoppedUntilTick >= expected, $"stopped until {tbar.StoppedUntilTick}, expected {expected}");
        if (crash.Severity == InjurySeverity.Minor)
            Assert.DoesNotContain(sim.State.Guests, g => g.Id == victim && g.Activity == RiderActivity.OnLift); // off the track
        var held = sim.State.Guests.Where(g => g.Activity == RiderActivity.OnLift).ToDictionary(g => g.Id, g => g.RouteProgressCm);
        long riders = tbar.Stats.Riders;
        sim.RunTicks(Math.Min(3, tbar.StoppedUntilTick - sim.State.Tick));
        Assert.Equal(riders, tbar.Stats.Riders);
        Assert.All(sim.State.Guests.Where(g => held.ContainsKey(g.Id) && g.Activity == RiderActivity.OnLift),
            g => Assert.Equal(held[g.Id], g.RouteProgressCm));

        // ... and runs again afterwards (unless the next crash stopped it again).
        sim.RunTicks(tbar.StoppedUntilTick - sim.State.Tick + 2);
        Assert.True(!tbar.IsStopped(sim.State.Tick) || tbar.StoppedUntilTick > expected);
    }
}
