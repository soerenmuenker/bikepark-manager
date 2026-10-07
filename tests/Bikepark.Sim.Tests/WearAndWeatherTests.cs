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

    /// <summary>Puts a built feature on a trail (as if the crew had finished it).</summary>
    private static TrailFeature AddFeature(Simulation sim, int wayId, string typeId, long distanceCm)
    {
        var feature = new TrailFeature { Id = sim.State.AllocateEntityId(), TypeId = typeId, DistanceCm = distanceCm, Built = true };
        var trail = Trail(sim, wayId);
        trail.Features.Add(feature);
        trail.Features.Sort((a, b) => a.DistanceCm.CompareTo(b.DistanceCm));
        sim.State.WaysRevision++;
        return feature;
    }

    [Fact]
    public void Riders_WearFeatures_NotTheTrail_MoreOnWetGround()
    {
        var sim = Valley(1);
        var berm = AddFeature(sim, FlowCountry, "berm", 20_000);
        var flat = AddFeature(sim, RedRocket, "table", 20_000);
        sim.RunTicks(12 * 60);
        Assert.True(berm.Condition < TrailCondition.Perfect);
        Assert.True(flat.Condition < TrailCondition.Perfect);
        Assert.All(sim.State.Ways, w => Assert.DoesNotContain(w.Features, f => f.Condition > TrailCondition.Perfect));

        var type = sim.State.TrailFeatureTypes.Single(t => t.Id == "berm");
        sim.State.Weather.WetnessPermille = 0;
        int dry = TrailCondition.PassWear(sim.State, type);
        sim.State.Weather.WetnessPermille = 1000;
        int wet = TrailCondition.PassWear(sim.State, type);
        Assert.Equal(sim.State.WearRules.WearPerPass * (1000 + type.Difficulty) / 1000, dry);
        Assert.InRange(wet, dry * 3 - 1, dry * 3 + 1);
    }

    [Fact]
    public void ATrailWithoutFeatures_NeverWearsOrCloses()
    {
        var sim = Valley(3 * GameTime.MinutesPerDay);
        Assert.True(sim.State.Ways.Sum(w => w.Stats.Runs) > 1000);
        Assert.All(sim.State.Ways, w => Assert.True(w.IsRideable));
    }

    [Fact]
    public void WornFeatures_AreSlowerAndLessFun()
    {
        var rules = TestWorlds.LiftScenario().WearRules;
        Assert.Equal(0, TrailCondition.SpeedLossPermille(rules, 800));
        Assert.Equal(0, TrailCondition.FunLoss(rules, 700));
        Assert.Equal(300, TrailCondition.SpeedLossPermille(rules, 0));
        Assert.Equal(400, TrailCondition.FunLoss(rules, 0));
        Assert.Equal(150, TrailCondition.SpeedLossPermille(rules, 350));
    }

    [Fact]
    public void WithoutWearRules_FeaturesStayPerfect()
    {
        var sim = new Simulation(TestWorlds.Create());
        foreach (var c in TestWorlds.DemoNetwork()) sim.Commands.Enqueue(c.Command, c.Tick);
        sim.Step();
        var feature = AddFeature(sim, 2, "berm", 18_000);
        sim.RunDays(1);
        Assert.True(sim.State.Ways.Sum(w => w.Stats.Runs) > 0);
        Assert.Equal(TrailCondition.Perfect, feature.Condition);
    }

    // ---------------------------------------------------------------- warning, closure and repairs

    [Fact]
    public void AFeatureBelowTheWarningLevel_RaisesOneWarning_AndNothingRepairsItself()
    {
        var sim = Valley(10 * 60);
        var berm = AddFeature(sim, RedRocket, "berm", 20_000);
        berm.Condition = 190_000; // 19 %
        var warnings = new List<FeatureWarning>();
        sim.Events.Clear();
        sim.Events.Subscribe<FeatureWarning>(warnings.Add);
        sim.RunTicks(120);
        sim.Events.Dispatch();

        var warning = Assert.Single(warnings);
        Assert.Equal((RedRocket, berm.Id), (warning.WayId, warning.FeatureId));
        Assert.True(berm.Warned);
        Assert.True(berm.Condition < 190_000); // riders keep riding it
        Assert.True(Trail(sim, RedRocket).IsRideable);
        Assert.Null(Jobs.ForRepair(sim.State, berm.Id)); // no automatic maintenance
    }

    [Fact]
    public void AFeatureAtZero_ClosesTheTrail_AndWarnsAgain()
    {
        var sim = Valley(10 * 60);
        var red = Trail(sim, RedRocket);
        var berm = AddFeature(sim, RedRocket, "berm", 20_000);
        berm.Condition = 0;
        sim.Events.Clear();
        var started = new List<RunStarted>();
        sim.Events.Subscribe<RunStarted>(started.Add);

        long closedAt = sim.State.Tick;
        sim.Step();
        Assert.True(red.WornOut);
        Assert.False(red.IsRideable);
        Assert.Contains(sim.Events.Pending, e => e is TrailClosed { WayId: RedRocket, Reason: TrailClosedReason.WornOut });
        Assert.Contains(sim.Events.Pending, e => e is FeatureWarning { WayId: RedRocket, ConditionPermille: 0 });

        sim.RunTicks(90);
        sim.Events.Dispatch();
        Assert.Contains(started, s => s.TrailId == FlowCountry);
        // Riders who picked it before (in the queue, on the lift) choose another trail at the top; only those already
        // on the trail finish it.
        Assert.DoesNotContain(started, s => s.TrailId == RedRocket && s.Tick > closedAt);
        Assert.True(red.Stats.ClosedMinutes >= 90);
    }

    [Fact]
    public void ARepair_ClosesTheTrailWhileTheCrewWorks_AndRestoresTheFeature()
    {
        var sim = Valley(7 * 60);
        var red = Trail(sim, RedRocket);
        var berm = AddFeature(sim, RedRocket, "berm", 20_000);
        berm.Condition = 100_000; // 10 %
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new RepairFeatureCommand(RedRocket, berm.Id, 2));
        sim.Step();

        var job = Jobs.ForRepair(sim.State, berm.Id);
        Assert.NotNull(job);
        Assert.Equal(job, sim.State.Jobs[0]); // front of the queue
        Assert.Equal(2, job.Workers);
        Assert.Equal("Repair Berm on Red Rocket at 200 m", Jobs.Title(sim.State, job));
        var type = sim.State.TrailFeatureTypes.Single(t => t.Id == "berm");
        Assert.Equal(type.WorkMinutes * sim.State.CrewRules.RepairWorkPermille / 1000 * 900_000 / 1_000_000, job.WorkMinutes);
        Assert.True(red.IsRideable); // the crew hasn't started yet (shift starts 07:30)

        var closed = new List<TrailClosed>();
        var reopened = new List<TrailReopened>();
        sim.Events.Clear();
        sim.Events.Subscribe<TrailClosed>(closed.Add);
        sim.Events.Subscribe<TrailReopened>(reopened.Add);
        sim.RunTicks(sim.State.CrewRules.WorkStartMinute - sim.State.Tick + 3);
        Assert.Equal(2, sim.State.Crew.Count(m => m.JobId == job.Id));
        Assert.True(red.Repairing);
        Assert.False(red.IsRideable);

        sim.RunTicks(GameTime.MinutesPerDay);
        sim.Events.Dispatch();
        Assert.Null(Jobs.ForRepair(sim.State, berm.Id));
        Assert.False(red.Repairing);
        Assert.True(red.IsRideable);
        Assert.Equal(1, red.Stats.Repairs);
        Assert.True(berm.Condition > 300_000); // perfect after the repair, worn a bit again by the riders since
        Assert.Contains(closed, c => c.WayId == RedRocket && c.Reason == TrailClosedReason.Repair);
        Assert.Contains(reopened, r => r.WayId == RedRocket);
    }

    [Fact]
    public void AWornOutTrail_ReopensOnlyWhenNoFeatureIsAtZero()
    {
        var sim = Valley(7 * 60);
        var red = Trail(sim, RedRocket);
        var a = AddFeature(sim, RedRocket, "berm", 20_000);
        var b = AddFeature(sim, RedRocket, "table", 40_000);
        a.Condition = 0;
        b.Condition = 0;
        sim.Commands.Enqueue(new HireCrewCommand());
        sim.Commands.Enqueue(new RepairFeatureCommand(RedRocket, a.Id, 1));
        sim.RunTicks(2 * GameTime.MinutesPerDay);
        Assert.Equal(TrailCondition.Perfect, a.Condition);
        Assert.True(red.WornOut); // the table is still at 0
        Assert.False(red.IsRideable);

        sim.Commands.Enqueue(new RepairFeatureCommand(RedRocket, b.Id, 1));
        sim.RunTicks(2 * GameTime.MinutesPerDay);
        Assert.False(red.WornOut);
        Assert.True(red.IsRideable);
    }

    [Fact]
    public void Commands_RepairAndClose()
    {
        var sim = Valley(1);
        var red = Trail(sim, RedRocket);
        var berm = AddFeature(sim, RedRocket, "berm", 20_000);
        Assert.Contains("perfect", Reject(sim, new RepairFeatureCommand(RedRocket, berm.Id)));
        Assert.Contains("trail", Reject(sim, new RepairFeatureCommand(8, 1))); // the hiking route is a path
        Assert.Contains("No such feature", Reject(sim, new RepairFeatureCommand(RedRocket, 999)));
        Assert.Contains("trail", Reject(sim, new SetTrailClosedCommand(999, true)));

        berm.Condition = 500_000;
        Assert.Contains("workers", Reject(sim, new RepairFeatureCommand(RedRocket, berm.Id, 9)));
        Assert.Null(Reject(sim, new RepairFeatureCommand(RedRocket, berm.Id)));
        var job = Jobs.ForRepair(sim.State, berm.Id)!;
        Assert.Equal(0, job.Workers);
        Assert.Equal(type(sim, "berm").WorkMinutes / 2 / 2, job.WorkMinutes); // half the wear, half the build work
        Assert.Contains("already", Reject(sim, new RepairFeatureCommand(RedRocket, berm.Id)));
        Assert.Null(Reject(sim, new CancelJobCommand(job.Id)));
        Assert.Null(Jobs.ForRepair(sim.State, berm.Id));

        Assert.Null(Reject(sim, new SetTrailClosedCommand(RedRocket, true)));
        Assert.Contains(sim.Events.Pending, e => e is TrailClosed { WayId: RedRocket, Reason: TrailClosedReason.Player });
        Assert.False(red.IsRideable);
        Assert.Null(Reject(sim, new SetTrailClosedCommand(RedRocket, false)));
        Assert.Contains(sim.Events.Pending, e => e is TrailReopened { WayId: RedRocket });
        Assert.True(red.IsRideable);

        static TrailFeatureType type(Simulation s, string id) => s.State.TrailFeatureTypes.Single(t => t.Id == id);
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
        Assert.NotEmpty(new WearRules { WarnBelowPermille = 2000 }.Validate());
    }

    [Fact]
    public void WearAndWeather_SurviveSaveAndLoad()
    {
        var sim = Valley(1);
        var berm = AddFeature(sim, RedRocket, "berm", 20_000);
        sim.RunTicks(GameTime.MinutesPerDay + 13 * 60);
        Trail(sim, RedRocket).Closed = true;
        var loaded = SaveGame.Deserialize(SaveGame.Serialize(sim.State));
        var red = loaded.Ways.Single(w => w.Id == RedRocket);
        Assert.Equal(berm.Condition, red.Features.Single().Condition);
        Assert.True(berm.Condition < TrailCondition.Perfect);
        Assert.True(red.Closed);
        Assert.Equal(sim.State.Weather.Today, loaded.Weather.Today);
        Assert.Equal(sim.State.Weather.Tomorrow, loaded.Weather.Tomorrow);
        Assert.Equal(sim.State.Weather.WetnessPermille, loaded.Weather.WetnessPermille);
        Assert.Equal(StateHash.Compute(sim.State), StateHash.Compute(loaded));
    }

    [Fact]
    public void AVersion2Save_IsMigrated()
    {
        var sim = Valley(1);
        AddFeature(sim, RedRocket, "berm", 20_000);
        string json = SaveGame.Serialize(sim.State);
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        root["version"] = 2;
        var world = root["world"]!.AsObject();
        foreach (var way in world["ways"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
        {
            way["condition"] = new System.Text.Json.Nodes.JsonArray(900_000);
            way["maintain"] = true;
        }
        world["wearRules"]!["closeBelowPermille"] = 250;
        world["wearRules"]!["maintainBelowPermille"] = 600;
        world["crewRules"]!["repairMinutesPerSegment"] = 20;

        var loaded = SaveGame.Deserialize(root.ToJsonString());
        Assert.Equal(TrailCondition.Perfect, loaded.Ways.Single(w => w.Id == RedRocket).Features.Single().Condition);
        Assert.Equal(500, loaded.CrewRules.RepairWorkPermille);
    }

    [Fact]
    public void NewCommands_HaveStableDiscriminators()
    {
        foreach (var (command, type) in new (ICommand, string)[]
                 {
                     (new RepairFeatureCommand(1, 2, 3), "repairFeature"),
                     (new SetTrailClosedCommand(1, true), "setTrailClosed"),
                 })
            Assert.Contains($"\"type\":\"{type}\"", System.Text.Json.JsonSerializer.Serialize(command, SimJson.Compact));
    }
}
