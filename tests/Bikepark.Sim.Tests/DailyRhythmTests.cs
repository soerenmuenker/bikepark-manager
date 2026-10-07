using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;

namespace Bikepark.Sim.Tests;

public class DailyRhythmTests
{
    // Starter Valley: open 09:00-18:00, last rides until 18:45, gondola warm-up from 08:30, crew 07:30-17:30 + 90 min
    // overtime, lunch planned between 11:00 and 13:00, no arrivals from 16:30.
    private const int Open = 9 * 60;
    private const int Close = 18 * 60;
    private const int LastRidesEnd = Close + 45;
    private const int Warmup = Open - 30;
    private const int CrewStart = 7 * 60 + 30;

    private static long At(int day, int minute) => day * (long)GameTime.MinutesPerDay + minute;

    // ---------------------------------------------------------------- timetable

    [Fact]
    public void Phases_FollowTheTimetable_AndTheNightIsQuiet()
    {
        var state = ScenarioLoader.CreateWorld(TestWorlds.LiftScenario());
        new Simulation(state).Step(); // builds the gondola, hires the two workers
        var crew = state.Crew.ToList();
        state.Crew.Clear();

        Assert.Equal(DayPhase.Night, ParkSchedule.Phase(state, At(0, 3 * 60)));
        Assert.True(ParkSchedule.IsQuiet(state, At(0, 3 * 60)));
        Assert.Equal(DayPhase.PreOpening, ParkSchedule.Phase(state, At(0, Warmup)));
        Assert.Equal(DayPhase.Night, ParkSchedule.Phase(state, At(0, Warmup - 1))); // no crew
        Assert.Equal(DayPhase.Open, ParkSchedule.Phase(state, At(0, Open)));
        Assert.Equal(DayPhase.LastRides, ParkSchedule.Phase(state, At(0, Close)));
        Assert.Equal(DayPhase.LastRides, ParkSchedule.Phase(state, At(0, LastRidesEnd - 1)));
        Assert.Equal(DayPhase.Night, ParkSchedule.Phase(state, At(0, LastRidesEnd)));

        Assert.Equal(At(0, Warmup), ParkSchedule.NextWakeTick(state, At(0, 3 * 60)));
        Assert.Equal(At(1, Warmup), ParkSchedule.NextWakeTick(state, At(0, Warmup)));
        Assert.Equal(At(1, Warmup), ParkSchedule.NextWakeTick(state, At(0, 20 * 60)));

        state.Crew.AddRange(crew);
        Assert.Equal(DayPhase.PreOpening, ParkSchedule.Phase(state, At(0, CrewStart)));
        Assert.Equal(DayPhase.Night, ParkSchedule.Phase(state, At(0, CrewStart - 1)));
        Assert.Equal(At(0, CrewStart), ParkSchedule.NextWakeTick(state, At(0, 3 * 60)));
        Assert.Equal(At(1, CrewStart), ParkSchedule.NextWakeTick(state, At(0, 20 * 60)));
        // Shift ends 17:30, before the last rides end: then it is night unless someone does overtime.
        Assert.Equal(DayPhase.Night, ParkSchedule.Phase(state, At(0, LastRidesEnd)));
        state.Crew[0].JobId = 1;
        Assert.Equal(DayPhase.AfterHours, ParkSchedule.Phase(state, At(0, LastRidesEnd)));
    }

    [Fact]
    public void DefaultRules_KeepTheOldDay()
    {
        var state = TestWorlds.Create();
        var rules = state.Rules;
        Assert.Equal(0, rules.LastRideMinutes);
        Assert.False(rules.HasLunch);
        Assert.Equal(DayPhase.Night, ParkSchedule.Phase(state, rules.CloseMinute));
        Assert.Equal(rules.OpenMinute, ParkSchedule.NextWakeTick(state, 0));
        Assert.Equal(1000, GuestArrivalSystem.ProfilePermille(rules.ArrivalProfile, 600));
    }

    [Fact]
    public void Rules_RejectBadTimetables()
    {
        Assert.NotEmpty(new ParkRules { LastRideMinutes = 300 }.Validate()); // past midnight
        Assert.NotEmpty(new ParkRules { LiftWarmupMinutes = -1 }.Validate());
        Assert.NotEmpty(new ParkRules { ArrivalProfile = [new(600, 1000), new(600, 500)] }.Validate());
        Assert.NotEmpty(new ParkRules { LunchStartMinute = 800, LunchEndMinute = 700 }.Validate());
        Assert.Contains(new CrewRules { OvertimeMinutes = 500 }.Validate(), e => e.Contains("overtime"));
        Assert.Empty(TestWorlds.LiftScenario().Rules.Validate());
    }

    // ---------------------------------------------------------------- guests

    [Fact]
    public void ArrivalProfile_IsInterpolated_AndNobodyArrivesWhenItIsZero()
    {
        List<ArrivalPoint> profile = [new(540, 1600), new(660, 1400), new(990, 0)];
        Assert.Equal(1600, GuestArrivalSystem.ProfilePermille(profile, 0));
        Assert.Equal(1500, GuestArrivalSystem.ProfilePermille(profile, 600));
        Assert.Equal(700, GuestArrivalSystem.ProfilePermille(profile, 825));
        Assert.Equal(0, GuestArrivalSystem.ProfilePermille(profile, 1000));

        var sim = TestWorlds.RunLift(1337, 0, TestWorlds.DemoLiftNetwork());
        sim.Events.Clear();
        var arrivals = new List<GuestArrived>();
        sim.Events.Subscribe<GuestArrived>(arrivals.Add);
        sim.RunDays(1);
        sim.Events.Dispatch();
        Assert.True(arrivals.Count > 200, $"{arrivals.Count} arrivals");
        Assert.All(arrivals, a => Assert.InRange(GameTime.MinuteOfDay(a.Tick), Open, 16 * 60 + 29));
        int morning = arrivals.Count(a => GameTime.MinuteOfDay(a.Tick) < 11 * 60);
        int afternoon = arrivals.Count(a => GameTime.MinuteOfDay(a.Tick) is >= 14 * 60 and < 16 * 60);
        Assert.True(morning > 2 * afternoon, $"morning {morning}, afternoon {afternoon}");
    }

    [Fact]
    public void Lunch_IsTakenOnceInTheWindow_PaidFor_AndDipsTheRiding()
    {
        var sim = TestWorlds.RunLift(1337, 0, TestWorlds.DemoLiftNetwork());
        sim.Events.Clear();
        var lunches = new List<GuestAteLunch>();
        sim.Events.Subscribe<GuestAteLunch>(lunches.Add);
        int eatingAt1230 = 0, eatingAt1000 = 0;
        for (int t = 0; t < GameTime.MinutesPerDay; t++)
        {
            sim.Step();
            int minute = GameTime.MinuteOfDay(sim.State.Tick);
            int eating = sim.State.Guests.Count(g => g.Activity == RiderActivity.Eating);
            if (minute == 12 * 60 + 30) eatingAt1230 = eating;
            if (minute == 10 * 60) eatingAt1000 = eating;
            Assert.All(sim.State.Guests.Where(g => g.Activity == RiderActivity.Eating), g => Assert.True(g.HadLunch));
        }
        sim.Events.Dispatch();

        Assert.Equal(0, eatingAt1000);
        Assert.True(eatingAt1230 > 20, $"{eatingAt1230} eating at 12:30");
        Assert.True(lunches.Count > 100, $"{lunches.Count} lunches");
        Assert.All(lunches, l => Assert.True(GameTime.MinuteOfDay(l.Tick) >= 11 * 60));
        Assert.Equal(lunches.Count, lunches.Select(l => l.GuestId).Distinct().Count());
        Assert.Equal(lunches.Sum(l => l.PaidCents), sim.State.Finance.TotalFoodCents);
        Assert.True(sim.State.Finance.TotalFoodCents > 0);
    }

    [Fact]
    public void Eating_RestoresEnergy_AndEndsAfterTheBreak()
    {
        var sim = TestWorlds.RunLift(1337, 11 * 60, TestWorlds.DemoLiftNetwork());
        var guest = sim.State.Guests.First(g => g.Activity == RiderActivity.Idle || g.Activity == RiderActivity.Queuing);
        guest.CashCents = 5_000;
        LiftSystem.LeaveQueue(sim.State, guest);
        RiderSystem.PlaceAtBase(sim.State, guest, sim.Network);
        guest.LunchMinute = 0;
        guest.HadLunch = false;
        guest.Energy = 300;
        long cash = guest.CashCents;

        sim.Step();
        Assert.Equal(RiderActivity.Eating, guest.Activity);
        Assert.Equal(300 + sim.State.Rules.LunchEnergy, guest.Energy);
        Assert.Equal(cash - sim.State.Rules.LunchPriceCents, guest.CashCents);
        Assert.InRange(guest.BusyUntilTick - sim.State.Tick + 1, sim.State.Rules.LunchMinMinutes, sim.State.Rules.LunchMaxMinutes);

        sim.RunTicks(guest.BusyUntilTick - sim.State.Tick + 1);
        Assert.NotEqual(RiderActivity.Eating, guest.Activity);
        Assert.Contains(sim.State.Guests, g => g.Id == guest.Id);
    }

    [Fact]
    public void AtClosing_RidersFinishTheirLap_AndThePark_IsEmptyAfterTheLastRides()
    {
        var sim = TestWorlds.RunLift(1337, Close, TestWorlds.DemoLiftNetwork());
        Assert.Contains(sim.State.Guests, g => g.Activity is RiderActivity.Descending or RiderActivity.OnLift or RiderActivity.Queuing);
        sim.Events.Clear();
        var runs = new List<RunFinished>();
        var left = new List<GuestLeft>();
        sim.Events.Subscribe<RunFinished>(runs.Add);
        sim.Events.Subscribe<GuestLeft>(left.Add);

        sim.Step(); // 18:00
        Assert.DoesNotContain(sim.State.Guests, g => g.Activity is RiderActivity.Idle or RiderActivity.Eating);
        Assert.NotEmpty(sim.State.Guests);

        sim.RunTicks(LastRidesEnd - Close);
        sim.Events.Dispatch();
        Assert.Empty(sim.State.Guests);
        Assert.Empty(sim.State.Lifts[0].Queue);
        Assert.True(runs.Count > 5, $"{runs.Count} runs after closing");
        Assert.All(runs, r => Assert.InRange(GameTime.MinuteOfDay(r.Tick), Close, LastRidesEnd - 1));
        Assert.All(left, l => Assert.Equal(GuestLeaveReason.ParkClosed, l.Reason));
    }

    // ---------------------------------------------------------------- lifts and crew

    [Fact]
    public void Gondola_WarmsUpEmpty_BeforeOpening()
    {
        var sim = TestWorlds.RunLift(1337, Warmup - 1);
        var lift = sim.State.Lifts[0];
        Assert.Equal(0, lift.CarriersDispatched);
        Assert.False(ParkSchedule.LiftRunning(sim.State, lift, sim.State.Tick));

        sim.RunTicks(Open - Warmup + 1);
        Assert.True(lift.CarriersDispatched > 0);
        Assert.Equal(0, lift.Stats.Riders);
        Assert.Empty(sim.State.Guests);
    }

    [Fact]
    public void Overtime_FinishesANearlyDoneJob_ButNotALongOne()
    {
        var sim = OvertimeWorld();
        var job = sim.State.Jobs[0];
        job.Progress = (job.WorkMinutes - 40) * 1000L; // 40 minutes left, 60 minutes of overtime
        sim.RunTicks(17 * 60 + 39 - sim.State.Tick);
        Assert.Equal(job.Id, sim.State.Crew[0].JobId);
        sim.Step();
        Assert.Empty(sim.State.Jobs);
        Assert.Equal(0, sim.State.Crew[0].JobId);

        sim = OvertimeWorld();
        job = sim.State.Jobs[0];
        job.Progress = (job.WorkMinutes - 100) * 1000L;
        long progress = job.Progress;
        sim.Step(); // 17:00: too much left, the worker goes home
        Assert.Equal(0, sim.State.Crew[0].JobId);
        sim.RunTicks(120);
        Assert.Equal(progress, job.Progress);
    }

    /// <summary>Test world with one worker on a double, 60 min overtime, at 16:59 (the last shift minute) after it ran.</summary>
    private static Simulation OvertimeWorld()
    {
        var scenario = TestWorlds.Scenario();
        scenario.CrewRules = new CrewRules { OvertimeMinutes = 60 };
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        foreach (var c in TestWorlds.DemoNetwork())
            sim.Commands.Enqueue(c.Command, 0);
        sim.Commands.Enqueue(new HireCrewCommand(), 0);
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(2, "double", 30_000), 0);
        sim.Step();
        sim.State.Jobs[0].WorkMinutes = 960; // a long job: the double alone would be done before 17:00
        sim.RunTicks(17 * 60 - sim.State.Tick);
        Assert.Single(sim.State.Jobs);
        Assert.NotEqual(0, sim.State.Crew[0].JobId);
        return sim;
    }
}
