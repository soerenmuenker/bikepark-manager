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
    // Starter Valley: open 09:00-18:00 for guests, lifts and crew alike; lunch planned between 11:00 and 13:00, no
    // arrivals from 16:30.
    private const int Open = 9 * 60;
    private const int Close = 18 * 60;

    private static long At(int day, int minute) => day * (long)GameTime.MinutesPerDay + minute;

    // ---------------------------------------------------------------- timetable

    [Fact]
    public void Phases_AreOpeningHoursThenLastRidesUntilTheParkIsEmpty()
    {
        var state = ScenarioLoader.CreateWorld(TestWorlds.LiftScenario());
        new Simulation(state).Step(); // builds the gondola, hires the two workers
        Assert.NotEmpty(state.Crew);

        Assert.Equal(DayPhase.Closed, ParkSchedule.Phase(state, At(0, 3 * 60)));
        Assert.True(ParkSchedule.IsQuiet(state, At(0, 3 * 60)));
        Assert.Equal(DayPhase.Closed, ParkSchedule.Phase(state, At(0, Open - 1)));
        Assert.Equal(DayPhase.Open, ParkSchedule.Phase(state, At(0, Open)));
        Assert.Equal(DayPhase.Open, ParkSchedule.Phase(state, At(0, Close - 1)));
        Assert.Equal(DayPhase.Closed, ParkSchedule.Phase(state, At(0, Close))); // empty: nothing left to ride
        Assert.True(ParkSchedule.IsQuiet(state, At(0, Close)));

        Assert.Equal(At(0, Open), ParkSchedule.NextWakeTick(state, At(0, 3 * 60)));
        Assert.Equal(At(1, Open), ParkSchedule.NextWakeTick(state, At(0, Open)));
        Assert.Equal(At(1, Open), ParkSchedule.NextWakeTick(state, At(0, 20 * 60)));

        // Someone still in the park after closing: last rides, no skipping, until they are gone.
        state.Guests.Add(new Guest { Id = 1 });
        Assert.Equal(DayPhase.LastRides, ParkSchedule.Phase(state, At(0, Close)));
        Assert.Equal(DayPhase.LastRides, ParkSchedule.Phase(state, At(0, 23 * 60 + 59)));
        Assert.False(ParkSchedule.IsQuiet(state, At(0, Close)));
        Assert.Equal(DayPhase.Closed, ParkSchedule.Phase(state, At(1, 3 * 60)));
    }

    [Fact]
    public void Rules_RejectBadTimetables()
    {
        Assert.NotEmpty(new ParkRules { ArrivalProfile = [new(600, 1000), new(600, 500)] }.Validate());
        Assert.NotEmpty(new ParkRules { LunchStartMinute = 800, LunchEndMinute = 700 }.Validate());
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

        var sim = TestWorlds.RunLift(1337, 0, [.. TestWorlds.DemoLiftNetwork(), .. TestWorlds.DemoFacilities()]);
        sim.State.ReputationRules.Enabled = false; // full demand, as in an established park
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
        var sim = TestWorlds.RunLift(1337, 0, [.. TestWorlds.DemoLiftNetwork(), .. TestWorlds.DemoFacilities()]);
        sim.State.ReputationRules.Enabled = false; // full demand, as in an established park
        sim.State.CrashRules.CollisionDistanceCm = 0; // the demo trails cross: nobody goes home hurt before lunch
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
        var sim = TestWorlds.RunLift(1337, 11 * 60, [.. TestWorlds.DemoLiftNetwork(), .. TestWorlds.DemoFacilities()]);
        // Riders come in waves (nobody overtakes): wait for one between laps or in the queue.
        while (!sim.State.Guests.Any(Waiting) && GameTime.MinuteOfDay(sim.State.Tick) < 12 * 60)
            sim.Step();
        var guest = sim.State.Guests.First(Waiting);
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

        static bool Waiting(Guest g) => g.Injury == Bikepark.Sim.Safety.InjurySeverity.None && g.Activity is RiderActivity.Idle or RiderActivity.Queuing;
    }

    [Fact]
    public void AtClosing_RidersFinishTheirLap_ThenTheParkIsEmptyAndQuiet()
    {
        // Without collisions on the crossing demo trails: a full park at closing.
        var sim = TestWorlds.RunLift(1337, Close, TestWorlds.DemoLiftNetwork(), s => s.CrashRules.CollisionDistanceCm = 0);
        Assert.Contains(sim.State.Guests, g => g.Activity is RiderActivity.Descending or RiderActivity.OnLift or RiderActivity.Queuing);
        sim.Events.Clear();
        var runs = new List<RunFinished>();
        var left = new List<GuestLeft>();
        var arrivals = new List<GuestArrived>();
        sim.Events.Subscribe<RunFinished>(runs.Add);
        sim.Events.Subscribe<GuestLeft>(left.Add);
        sim.Events.Subscribe<GuestArrived>(arrivals.Add);

        var boarded = new List<RiderBoarded>();
        sim.Events.Subscribe<RiderBoarded>(boarded.Add);

        sim.Step(); // 18:00: guests between laps and in the queue go home, nobody new comes
        Assert.DoesNotContain(sim.State.Guests, g => g.Activity is RiderActivity.Idle or RiderActivity.Eating or RiderActivity.Queuing);
        Assert.Equal(DayPhase.LastRides, ParkSchedule.Phase(sim.State, sim.State.Tick));

        int guard = 0;
        while (sim.State.Guests.Count > 0 && guard++ < 3 * 60)
            sim.Step();
        sim.Events.Dispatch();

        Assert.Empty(sim.State.Guests);
        Assert.Empty(sim.State.Lifts[0].Queue);
        Assert.Empty(arrivals);
        Assert.Empty(boarded); // the lift only finished those already aboard
        Assert.True(runs.Count > 5, $"{runs.Count} runs after closing");
        Assert.All(left, l => Assert.Equal(GuestLeaveReason.ParkClosed, l.Reason));
        Assert.True(ParkSchedule.IsQuiet(sim.State, sim.State.Tick));
        Assert.False(ParkSchedule.LiftRunning(sim.State, sim.State.Lifts[0], sim.State.Tick));
    }

    // ---------------------------------------------------------------- lifts and crew

    [Fact]
    public void Gondola_RunsOnlyInOpeningHours()
    {
        var sim = TestWorlds.RunLift(1337, Open - 1);
        var lift = sim.State.Lifts[0];
        Assert.Equal(0, lift.CarriersDispatched);
        Assert.False(ParkSchedule.LiftRunning(sim.State, lift, sim.State.Tick));

        sim.RunTicks(2);
        Assert.True(ParkSchedule.LiftRunning(sim.State, lift, sim.State.Tick));
        Assert.True(lift.CarriersDispatched > 0);
    }

    [Fact]
    public void Crew_WorksOnlyInOpeningHours()
    {
        var scenario = TestWorlds.Scenario();
        var sim = new Simulation(ScenarioLoader.CreateWorld(scenario));
        foreach (var c in TestWorlds.DemoNetwork())
            sim.Commands.Enqueue(c.Command, 0);
        sim.Commands.Enqueue(new HireCrewCommand(), 0);
        sim.Commands.Enqueue(new PlaceTrailFeatureCommand(2, "double", 30_000), 0);
        sim.Step();
        var job = sim.State.Jobs[0];
        job.WorkMinutes = 960; // a long job: still running at closing

        int open = sim.State.Rules.OpenMinute, close = sim.State.Rules.CloseMinute;
        sim.RunTicks(open - 1 - sim.State.Tick);
        Assert.Equal(0, sim.State.Crew[0].JobId);
        Assert.Equal(0, job.Progress);

        sim.RunTicks(2);
        Assert.Equal(job.Id, sim.State.Crew[0].JobId);

        sim.RunTicks(close + 1 - sim.State.Tick);
        Assert.Equal(0, sim.State.Crew[0].JobId);
        long progress = job.Progress;
        Assert.True(progress > 0);
        sim.RunTicks(120);
        Assert.Equal(progress, job.Progress);
    }
}
