using Bikepark.Sim.Commands;
using Bikepark.Sim.Core;
using Bikepark.Sim.Crew;
using Bikepark.Sim.Events;
using Bikepark.Sim.Persistence;
using Bikepark.Sim.Scenarios;
using Bikepark.Sim.State;
using Bikepark.Sim.Systems;
using Bikepark.Sim.Trails;
using Bikepark.Sim.Weather;

namespace Bikepark.Sim.Tests;

public class WearAndWeatherTests
{
    // Starter Valley + demo_lift_network.json: Flow Country 11, Red Rocket 12.
    private const int FlowCountry = 11;
    private const int RedRocket = 12;

    private static Way Trail(Simulation sim, int id) => sim.State.Ways.Single(w => w.Id == id);

    private static Simulation Valley(long ticks) => TestWorlds.RunLift(1337, ticks, TestWorlds.DemoLiftNetwork());

    private static string? Reject(Simulation sim, ICommand command)
    {
        sim.Events.Clear();
        sim.Commands.Enqueue(command);
        sim.Step();
        return sim.Events.Pending.OfType<CommandRejected>().SingleOrDefault()?.Reason;
    }

    // ---------------------------------------------------------------- weather

    [Fact]
    public void WithoutWeatherOdds_ItStaysSunny()
    {
        var sim = new Simulation(TestWorlds.Create());
        sim.RunDays(2);
        Assert.Equal(DayWeather.Sunny, sim.State.Weather.Today);
        Assert.Null(sim.State.Weather.Tomorrow);
        Assert.Equal(0, sim.State.Weather.RainDays);
        Assert.Equal(0, sim.State.Weather.WetnessPermille);
    }

    [Fact]
    public void RolledWeather_FollowsTheOdds_AndRainsInItsWindow()
    {
        var rules = TestWorlds.LiftScenario().WeatherRules;
        var rng = SimRandom.FromSeed(7);
        var days = Enumerable.Range(0, 4000).Select(_ => WeatherMath.Roll(rng, rules)).ToList();

        int Count(WeatherKind kind) => days.Count(d => d.Kind == kind);
        Assert.InRange(Count(WeatherKind.Sunny), 1450, 1750);   // 400 ‰
        Assert.InRange(Count(WeatherKind.Cloudy), 1050, 1350);  // 300 ‰
        Assert.InRange(Count(WeatherKind.Showers), 650, 950);   // 200 ‰
        Assert.InRange(Count(WeatherKind.Rain), 280, 520);      // 100 ‰
        Assert.All(days.Where(d => d.Kind is WeatherKind.Sunny or WeatherKind.Cloudy), d => Assert.False(d.HasRain));
        Assert.All(days.Where(d => d.Kind == WeatherKind.Showers), d =>
        {
            Assert.InRange(d.RainEndMinute - d.RainStartMinute, 60, 180);
            Assert.True(d.RainStartMinute >= 6 * 60 && d.RainEndMinute <= 20 * 60);
        });
        Assert.All(days.Where(d => d.Kind == WeatherKind.Rain), d =>
        {
            Assert.InRange(d.RainEndMinute - d.RainStartMinute, 240, 600);
            Assert.True(d.RainStartMinute >= 3 * 60 && d.RainEndMinute <= 22 * 60);
        });
    }

    [Fact]
    public void TheForecast_BecomesTomorrowsWeather()
    {
        var sim = Valley(GameTime.MinutesPerDay - 1);
        var forecast = sim.State.Weather.Tomorrow;
        Assert.NotNull(forecast);
        var forecasts = new List<WeatherForecast>();
        sim.Events.Clear();
        sim.Events.Subscribe<WeatherForecast>(forecasts.Add);
        sim.RunTicks(2);
        sim.Events.Dispatch();
        Assert.Equal(forecast, sim.State.Weather.Today);
        Assert.Equal(forecast, Assert.Single(forecasts).Today);
    }

    [Fact]
    public void Rain_WetsTheGround_WhichDriesAfterwards_AndKeepsGuestsAway()
    {
        var sim = Valley(1);
        var weather = sim.State.Weather;
        weather.Today = new DayWeather(WeatherKind.Rain, 60, 180);
        weather.WetnessPermille = 0;
        sim.RunTicks(60 + 120 - sim.State.Tick);
        Assert.Equal(Math.Min(1000, 120 * sim.State.WeatherRules.WetPerRainMinute), weather.WetnessPermille);
        Assert.True(WeatherMath.IsRaining(sim.State, 179));
        sim.RunTicks(60);
        Assert.Equal(960 - 60 * sim.State.WeatherRules.DryPerMinuteCloudy, weather.WetnessPermille);

        int rainy = GuestArrivalSystem.ExpectedArrivalsPermille(sim.State, 600);
        weather.Today = DayWeather.Sunny;
        int sunny = GuestArrivalSystem.ExpectedArrivalsPermille(sim.State, 600);
        Assert.InRange(rainy, sunny * 450 / 1050 - 2, sunny * 450 / 1050 + 2);
    }

    // ---------------------------------------------------------------- wear

    [Fact]
    public void Riders_WearTheTrails_MoreOnWetGround()
    {
        var sim = Valley(12 * 60);
        var flow = Trail(sim, FlowCountry);
        Assert.NotEmpty(flow.Condition);
        Assert.Contains(flow.Condition, c => c < TrailCondition.Perfect);
        Assert.All(sim.State.Ways.Where(w => w.Kind == WayKind.AccessPath), w => Assert.Empty(w.Condition));

        var segment = sim.Network.Geometry(FlowCountry).Segments[3];
        sim.State.Weather.WetnessPermille = 0;
        int dry = TrailCondition.PassWear(sim.State, segment);
        sim.State.Weather.WetnessPermille = 1000;
        int wet = TrailCondition.PassWear(sim.State, segment);
        Assert.Equal(60 * (1000 + segment.Difficulty) / 1000, dry);
        Assert.InRange(wet, dry * 3 - 1, dry * 3 + 1);
    }

    [Fact]
    public void WornSegments_AreSlowerAndLessFun()
    {
        var rules = TestWorlds.LiftScenario().WearRules;
        Assert.Equal(0, TrailCondition.SpeedLossPermille(rules, 800));
        Assert.Equal(0, TrailCondition.FunLoss(rules, 700));
        Assert.Equal(300, TrailCondition.SpeedLossPermille(rules, 0));
        Assert.Equal(400, TrailCondition.FunLoss(rules, 0));
        Assert.Equal(150, TrailCondition.SpeedLossPermille(rules, 350));
    }

    [Fact]
    public void WithoutWearRules_TrailsStayPerfect()
    {
        var sim = new Simulation(TestWorlds.Create());
        foreach (var c in TestWorlds.DemoNetwork()) sim.Commands.Enqueue(c.Command, c.Tick);
        sim.RunDays(1);
        Assert.True(sim.State.Ways.Sum(w => w.Stats.Runs) > 0);
        Assert.All(sim.State.Ways, w => Assert.Empty(w.Condition));
    }

    // ---------------------------------------------------------------- closures and repairs

    [Fact]
    public void AWornOutTrail_Closes_AndRidersPickAnother()
    {
        var sim = Valley(10 * 60);
        var red = Trail(sim, RedRocket);
        red.Maintain = false;
        TrailCondition.Wear(red, 5, TrailCondition.Perfect - 200_000); // 200 ‰
        sim.Events.Clear();
        var started = new List<RunStarted>();
        sim.Events.Subscribe<RunStarted>(started.Add);

        long closedAt = sim.State.Tick;
        sim.Step();
        Assert.True(red.WornOut);
        Assert.False(red.IsRideable);
        Assert.Contains(sim.Events.Pending, e => e is TrailClosed { WayId: RedRocket, WornOut: true });
        Assert.Null(Jobs.ForRepair(sim.State, RedRocket)); // not maintained

        sim.RunTicks(90);
        sim.Events.Dispatch();
        Assert.Contains(started, s => s.TrailId == FlowCountry);
        // Riders who picked it before (in the queue, on the lift) still ride it; nobody picks it any more.
        Assert.DoesNotContain(started, s => s.TrailId == RedRocket && s.Tick > closedAt + 60);
        Assert.True(red.Stats.ClosedMinutes >= 90);
    }

    [Fact]
    public void MaintainedTrail_GetsARepairJob_AndTheCrewRestoresIt()
    {
        var sim = Valley(8 * 60);
        var red = Trail(sim, RedRocket);
        for (int i = 0; i < 10; i++)
            TrailCondition.Wear(red, i, TrailCondition.Get(red, i) - 100_000); // ten segments at 100 ‰
        sim.Step();
        Assert.True(red.WornOut);
        var job = Jobs.ForRepair(sim.State, RedRocket);
        Assert.NotNull(job);
        Assert.Equal("Repair Red Rocket", Jobs.Title(sim.State, job));
        long expected = WorkCosts.RepairMinutes(sim.State.CrewRules, red, sim.Network.Geometry(RedRocket).Segments.Count);
        Assert.Equal(expected, job.WorkMinutes);
        Assert.True(job.WorkMinutes >= 10 * 18); // 900 ‰ missing on ten segments at 20 min each

        var reopened = new List<TrailReopened>();
        sim.Events.Clear();
        sim.Events.Subscribe<TrailReopened>(reopened.Add);
        sim.RunTicks(GameTime.MinutesPerDay);
        sim.Events.Dispatch();
        Assert.Null(Jobs.ForRepair(sim.State, RedRocket));
        Assert.False(red.WornOut);
        Assert.Equal(1, red.Stats.Repairs);
        Assert.Contains(reopened, r => r.WayId == RedRocket);
        Assert.True(TrailCondition.WorstPermille(red, sim.Network.Geometry(RedRocket).Segments.Count) > 800);
    }

    [Fact]
    public void Commands_RepairCloseAndMaintain()
    {
        var sim = Valley(1);
        Assert.Contains("perfect", Reject(sim, new RepairTrailCommand(RedRocket)));
        Assert.Contains("trail", Reject(sim, new RepairTrailCommand(8))); // the hiking route is a path
        Assert.Contains("trail", Reject(sim, new SetTrailClosedCommand(999, true)));

        var red = Trail(sim, RedRocket);
        TrailCondition.Wear(red, 0, 500_000);
        Assert.Null(Reject(sim, new RepairTrailCommand(RedRocket)));
        var job = Jobs.ForRepair(sim.State, RedRocket)!;
        Assert.Equal(10, job.WorkMinutes); // half a segment at 20 min
        Assert.Contains("already", Reject(sim, new RepairTrailCommand(RedRocket)));

        Assert.Null(Reject(sim, new CancelJobCommand(job.Id)));
        Assert.False(red.Maintain); // or it would come straight back
        Assert.Null(Reject(sim, new SetTrailMaintainCommand(RedRocket, true)));
        Assert.True(red.Maintain);

        Assert.Null(Reject(sim, new SetTrailClosedCommand(RedRocket, true)));
        Assert.Contains(sim.Events.Pending, e => e is TrailClosed { WayId: RedRocket, WornOut: false });
        Assert.False(red.IsRideable);
        Assert.Null(Reject(sim, new SetTrailClosedCommand(RedRocket, false)));
        Assert.Contains(sim.Events.Pending, e => e is TrailReopened { WayId: RedRocket });
        Assert.True(red.IsRideable);
    }

    [Fact]
    public void WornOutTrail_StaysClosed_WhenThePlayerOpensIt()
    {
        var sim = Valley(1);
        var red = Trail(sim, RedRocket);
        red.WornOut = true;
        Assert.Null(Reject(sim, new SetTrailClosedCommand(RedRocket, false)));
        Assert.DoesNotContain(sim.Events.Pending, e => e is TrailReopened);
        Assert.False(red.IsRideable);
    }

    // ---------------------------------------------------------------- content and saves

    [Fact]
    public void Rules_AreValidated()
    {
        Assert.Empty(TestWorlds.LiftScenario().WeatherRules.Validate());
        Assert.Empty(TestWorlds.LiftScenario().WearRules.Validate());
        Assert.NotEmpty(new WeatherRules { SunnyPermille = 500 }.Validate()); // odds must add up to 1000
        Assert.NotEmpty(new WeatherRules { RainMinHours = 5, RainMaxHours = 2 }.Validate());
        Assert.NotEmpty(new WearRules { WearPerPass = -1 }.Validate());
        Assert.NotEmpty(new WearRules { CloseBelowPermille = 2000 }.Validate());
    }

    [Fact]
    public void WearAndWeather_SurviveSaveAndLoad()
    {
        var sim = Valley(GameTime.MinutesPerDay + 13 * 60);
        Trail(sim, RedRocket).Closed = true;
        var loaded = SaveGame.Deserialize(SaveGame.Serialize(sim.State));
        var red = loaded.Ways.Single(w => w.Id == RedRocket);
        Assert.Equal(Trail(sim, RedRocket).Condition, red.Condition);
        Assert.True(red.Closed);
        Assert.Equal(sim.State.Weather.Today, loaded.Weather.Today);
        Assert.Equal(sim.State.Weather.Tomorrow, loaded.Weather.Tomorrow);
        Assert.Equal(sim.State.Weather.WetnessPermille, loaded.Weather.WetnessPermille);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded));
    }

    [Fact]
    public void NewCommands_HaveStableDiscriminators()
    {
        foreach (var (command, type) in new (ICommand, string)[]
                 {
                     (new RepairTrailCommand(1), "repairTrail"),
                     (new SetTrailClosedCommand(1, true), "setTrailClosed"),
                     (new SetTrailMaintainCommand(1, false), "setTrailMaintain"),
                 })
            Assert.Contains($"\"type\":\"{type}\"", System.Text.Json.JsonSerializer.Serialize(command, SimJson.Compact));
    }
}
