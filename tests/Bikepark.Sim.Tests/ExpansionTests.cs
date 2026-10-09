using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Events;
using Bikepark.Sim.Lifts;
using Bikepark.Sim.Persistence;
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
}
